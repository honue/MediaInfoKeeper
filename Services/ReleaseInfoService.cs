using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Options;

namespace MediaInfoKeeper.Services {
    internal sealed class ReleaseInfoService : IDisposable {
        private const int ReleasePageSize = 5;
        private const int MaxReleasePages = 1;

        private static readonly TimeSpan PeriodicRefreshInterval = TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNameCaseInsensitive = true
        };

        private readonly Func<PluginConfiguration> getOptions;

        private readonly IHttpClient httpClient;
        private readonly ILogger logger;
        private readonly object syncRoot = new();
        private bool disposed;
        private string latestVersionCache;
        private bool refreshInProgress;

        private Timer refreshTimer;
        private List<ReleaseInfo> releaseCache = new();
        private string releaseRepositoryCache;
        private string releaseHistoryBodyCache;
        private string releaseHistoryChannelCache;

        public ReleaseInfoService(IHttpClient httpClient, ILogger logger, Func<PluginConfiguration> getOptions) {
            this.httpClient = httpClient;
            this.logger = logger;
            this.getOptions = getOptions;
        }

        public string LatestVersion {
            get {
                EnsureHistoryForCurrentChannel();
                lock (syncRoot) {
                    return latestVersionCache;
                }
            }
        }

        public string HistoryBody {
            get {
                EnsureHistoryForCurrentChannel();
                lock (syncRoot) {
                    return releaseHistoryBodyCache;
                }
            }
        }

        public void Dispose() {
            lock (syncRoot) {
                if (disposed) return;

                disposed = true;
                refreshTimer?.Dispose();
                refreshTimer = null;
            }
        }

        public void Start() {
            lock (syncRoot) {
                if (refreshTimer != null) return;

                refreshTimer = new Timer(
                    _ => RefreshInBackground(),
                    null,
                    TimeSpan.Zero,
                    PeriodicRefreshInterval);
            }
        }

        private void RefreshInBackground() {
            var shouldStart = false;
            lock (syncRoot) {
                if (!disposed && !refreshInProgress) {
                    refreshInProgress = true;
                    shouldStart = true;
                }
            }

            if (!shouldStart) return;

            Task.Run(() => {
                try {
                    RefreshCache();
                }
                catch (Exception ex) {
                    logger.Info($"后台获取 GitHub 历史版本失败: {ex.Message}");
                    logger.Debug(ex.StackTrace);
                }
                finally {
                    lock (syncRoot) {
                        refreshInProgress = false;
                    }
                }
            });
        }

        public void RefreshCache() {
            var options = getOptions()?.GetEffectiveUpdatePluginOptions();
            var currentChannel = options?.UpdateChannel ?? MainPageOptions.UpdateChannelOption.Stable.ToString();
            var source = PluginUpdateSource.Resolve(currentChannel, options?.BetaRepositoryUrl);
            var releases = FetchAllReleasesAsync(CancellationToken.None, options?.GitHubToken, source)
                .GetAwaiter().GetResult();
            StoreReleaseCache(releases, currentChannel, source);
        }

        public static DateTimeOffset GetReleaseSortTime(string publishedAt, string createdAt) {
            if (DateTimeOffset.TryParse(publishedAt, out var publishedOffset)) return publishedOffset;

            if (DateTimeOffset.TryParse(createdAt, out var createdOffset)) return createdOffset;

            return DateTimeOffset.MinValue;
        }

        public async Task<ReleaseInfo> RefreshAndSelectReleaseForChannelAsync(
            CancellationToken cancellationToken,
            string updateChannel,
            string githubToken,
            PluginUpdateSource source) {
            var releases = await FetchAllReleasesAsync(cancellationToken, githubToken, source).ConfigureAwait(false);
            StoreReleaseCache(releases, updateChannel, source);
            return SelectReleaseForChannel(releases, updateChannel);
        }

        public ReleaseInfo SelectCachedReleaseForChannel(string updateChannel, PluginUpdateSource source) {
            List<ReleaseInfo> releases;
            lock (syncRoot) {
                if (!string.Equals(releaseRepositoryCache, source.Repository, StringComparison.OrdinalIgnoreCase) ||
                    releaseCache.Count == 0)
                    return null;

                releases = releaseCache.ToList();
            }

            return SelectReleaseForChannel(releases, updateChannel);
        }

        private static ReleaseInfo SelectReleaseForChannel(
            IEnumerable<ReleaseInfo> releases,
            string updateChannel) {
            var preferBeta = string.Equals(
                updateChannel,
                MainPageOptions.UpdateChannelOption.Beta.ToString(),
                StringComparison.OrdinalIgnoreCase);
            var candidates = releases?
                .Where(r => r != null && !r.draft)
                .OrderByDescending(r => GetReleaseSortTime(r?.published_at, r?.created_at))
                .ToList() ?? new List<ReleaseInfo>();
            return preferBeta
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(r => !r.prerelease);
        }

        private ReleaseHistoryInfo BuildReleaseHistoryInfo(
            IEnumerable<ReleaseInfo> releaseItems,
            string updateChannel) {
            try {
                var latestVersion = "未知";
                var preferBeta = string.Equals(
                    updateChannel,
                    MainPageOptions.UpdateChannelOption.Beta.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                var releases = new List<ReleaseHistoryEntry>();
                foreach (var release in releaseItems ?? Enumerable.Empty<ReleaseInfo>()) {
                    if (release == null || release.draft) continue;

                    var tag = release.tag_name ?? string.Empty;
                    var name = release.name ?? string.Empty;
                    var body = release.body ?? string.Empty;
                    var publishedAt = release.published_at ?? string.Empty;
                    var createdAt = release.created_at ?? string.Empty;
                    var publishedAtLocal = publishedAt;
                    if (!string.IsNullOrWhiteSpace(publishedAt) &&
                        DateTimeOffset.TryParse(publishedAt, out var publishedOffset))
                        publishedAtLocal = ConfiguredDateTime
                            .ToConfiguredOffset(publishedOffset)
                            .ToString("yyyy-MM-dd HH:mm:ss");

                    if (string.IsNullOrWhiteSpace(body)) body = "无更新说明";

                    releases.Add(new ReleaseHistoryEntry(
                        tag,
                        name,
                        body,
                        publishedAtLocal,
                        release.prerelease,
                        GetReleaseSortTime(publishedAt, createdAt)));
                }

                var orderedReleases = releases
                    .OrderByDescending(r => r.SortTime)
                    .ThenByDescending(r => r.Tag ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(r => r.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var latestRelease = preferBeta
                    ? orderedReleases.FirstOrDefault()
                    : orderedReleases.FirstOrDefault(r => !r.IsPrerelease);
                if (latestRelease != null)
                    latestVersion = !string.IsNullOrWhiteSpace(latestRelease.Tag) ? latestRelease.Tag.Trim()
                        : !string.IsNullOrWhiteSpace(latestRelease.Name) ? latestRelease.Name.Trim()
                        : "未知";

                var sb = new StringBuilder();
                foreach (var release in orderedReleases) {
                    sb.Append(string.IsNullOrWhiteSpace(release.Tag) ? "Release" : release.Tag.Trim());
                    if (!string.IsNullOrWhiteSpace(release.Name)) sb.Append(" - ").Append(release.Name.Trim());

                    if (release.IsPrerelease) sb.Append(" [Prerelease]");

                    if (!string.IsNullOrWhiteSpace(release.PublishedAtLocal))
                        sb.Append(" (").Append(release.PublishedAtLocal.Trim()).Append(')');

                    sb.AppendLine();
                    sb.AppendLine(release.Body.Trim());
                    sb.AppendLine("----");
                }

                return sb.Length == 0
                    ? new ReleaseHistoryInfo("暂无发布记录", latestVersion, true)
                    : new ReleaseHistoryInfo(sb.ToString().TrimEnd(), latestVersion, true);
            }
            catch (Exception ex) {
                logger.Info($"构建 GitHub 历史版本信息失败: {ex.Message}");
                logger.Debug(ex.StackTrace);
                return new ReleaseHistoryInfo("获取失败", "获取失败", false);
            }
        }

        private void EnsureHistoryForCurrentChannel() {
            var options = getOptions()?.GetEffectiveUpdatePluginOptions();
            var currentChannel = options?.UpdateChannel ?? MainPageOptions.UpdateChannelOption.Stable.ToString();
            PluginUpdateSource source;
            try {
                source = PluginUpdateSource.Resolve(currentChannel, options?.BetaRepositoryUrl);
            }
            catch (ArgumentException ex) {
                lock (syncRoot) {
                    releaseRepositoryCache = null;
                    latestVersionCache = "获取失败";
                    releaseHistoryBodyCache = ex.Message;
                }

                return;
            }

            var shouldRefresh = false;
            lock (syncRoot) {
                if (!string.Equals(releaseRepositoryCache, source.Repository, StringComparison.OrdinalIgnoreCase)) {
                    latestVersionCache = null;
                    releaseHistoryBodyCache = null;
                    shouldRefresh = true;
                }
                else if (!string.Equals(releaseHistoryChannelCache, currentChannel, StringComparison.OrdinalIgnoreCase)) {
                    var historyInfo = BuildReleaseHistoryInfo(releaseCache, currentChannel);
                    releaseHistoryChannelCache = currentChannel;
                    releaseHistoryBodyCache = historyInfo.HistoryBody;
                    latestVersionCache = historyInfo.LatestVersion;
                }
            }

            if (shouldRefresh) RefreshInBackground();
        }

        private void StoreReleaseCache(List<ReleaseInfo> releases, string updateChannel, PluginUpdateSource source) {
            var historyInfo = BuildReleaseHistoryInfo(releases, updateChannel);
            lock (syncRoot) {
                releaseCache = releases ?? new List<ReleaseInfo>();
                releaseRepositoryCache = source.Repository;
                releaseHistoryChannelCache = updateChannel;
                releaseHistoryBodyCache = historyInfo.HistoryBody;
                latestVersionCache = historyInfo.LatestVersion;
            }
        }

        private async Task<List<ReleaseInfo>> FetchAllReleasesAsync(
            CancellationToken cancellationToken,
            string githubToken,
            PluginUpdateSource source) {
            var releases = new List<ReleaseInfo>();
            for (var page = 1; page <= MaxReleasePages; page++) {
                var releaseResults = await FetchReleasePageAsync(page, cancellationToken, githubToken, source)
                    .ConfigureAwait(false);
                releases.AddRange(releaseResults);
                if (releaseResults.Count < ReleasePageSize) break;
            }

            return releases;
        }

        private async Task<List<ReleaseInfo>> FetchReleasePageAsync(
            int page,
            CancellationToken cancellationToken,
            string githubToken,
            PluginUpdateSource source) {
            var releaseRequestOptions = new HttpRequestOptions {
                Url = source.GetReleasesUrl(page, ReleasePageSize),
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                UserAgent = "MediaInfoKeeper",
                EnableDefaultUserAgent = false,
                LogRequest = true,
                LogResponse = true
            };
            AddRefetchHeaders(releaseRequestOptions);
            if (!string.IsNullOrWhiteSpace(githubToken)) releaseRequestOptions.RequestHeaders["Authorization"] = $"token {githubToken}";

            using var response = await httpClient.SendAsync(releaseRequestOptions, "GET").ConfigureAwait(false);
            string releaseResponseBody;
            await using (var contentStream = response.Content)
            using (var reader = new StreamReader(contentStream)) {
                releaseResponseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300) {
                logger.Error("获取 Release 失败：repository={0}, page={1}, status={2}, body={3}", source.Repository, page,
                    (int)response.StatusCode, releaseResponseBody);
                throw new Exception($"获取 Release 失败: {(int)response.StatusCode}");
            }

            return JsonSerializer.Deserialize<List<ReleaseInfo>>(releaseResponseBody, JsonOptions) ??
                   new List<ReleaseInfo>();
        }

        private static void AddRefetchHeaders(HttpRequestOptions requestOptions) {
            requestOptions.RequestHeaders["Cache-Control"] = "no-cache";
            requestOptions.RequestHeaders["Pragma"] = "no-cache";
        }

        internal sealed class ReleaseAssetInfo {
            public string name { get; set; }

            public string browser_download_url { get; set; }
        }

        internal sealed class ReleaseInfo {
            public string tag_name { get; set; }

            public string name { get; set; }

            public string body { get; set; }

            public bool prerelease { get; set; }

            public bool draft { get; set; }

            public string published_at { get; set; }

            public string created_at { get; set; }

            public List<ReleaseAssetInfo> assets { get; set; }
        }

        private readonly struct ReleaseHistoryInfo {
            public ReleaseHistoryInfo(string historyBody, string latestVersion, bool isSuccess) {
                HistoryBody = historyBody;
                LatestVersion = latestVersion;
                IsSuccess = isSuccess;
            }

            public string HistoryBody { get; }

            public string LatestVersion { get; }

            public bool IsSuccess { get; }
        }

        private sealed class ReleaseHistoryEntry {
            public ReleaseHistoryEntry(
                string tag,
                string name,
                string body,
                string publishedAtLocal,
                bool isPrerelease,
                DateTimeOffset sortTime) {
                Tag = tag;
                Name = name;
                Body = body;
                PublishedAtLocal = publishedAtLocal;
                IsPrerelease = isPrerelease;
                SortTime = sortTime;
            }

            public string Tag { get; }

            public string Name { get; }

            public string Body { get; }

            public string PublishedAtLocal { get; }

            public bool IsPrerelease { get; }

            public DateTimeOffset SortTime { get; }
        }
    }
}
