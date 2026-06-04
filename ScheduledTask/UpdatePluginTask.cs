using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{


    public class UpdatePluginTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly IApplicationHost applicationHost;
        private readonly IApplicationPaths applicationPaths;
        private readonly IHttpClient httpClient;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IActivityManager activityManager;
        private readonly IServerApplicationHost serverApplicationHost;
        private static string PluginAssemblyFilename => Assembly.GetExecutingAssembly().GetName().Name + ".dll";
        private static string RepoVersionUrl => "https://raw.githubusercontent.com/honue/MediaInfoKeeper/master/Version.json";

        public string Key => "UpdatePluginTask";

        public string Name => "01.更新插件";

        public string Description => "更新插件至最新版本";

        public string Category => Plugin.TaskCategoryName;

        public UpdatePluginTask(
            IApplicationHost applicationHost,
            IApplicationPaths applicationPaths,
            IHttpClient httpClient,
            IJsonSerializer jsonSerializer,
            IActivityManager activityManager,
            IServerApplicationHost serverApplicationHost)
        {
            this.logger = Plugin.Instance.Logger;
            this.applicationHost = applicationHost;
            this.applicationPaths = applicationPaths;
            this.httpClient = httpClient;
            this.jsonSerializer = jsonSerializer;
            this.activityManager = activityManager;
            this.serverApplicationHost = serverApplicationHost;
        }


        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Monday,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            };

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Thursday,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Task.Yield();
            progress.Report(0);

            try
            {
                var updatePluginOptions = Plugin.Instance.Options.GetEffectiveUpdatePluginOptions();
                var githubToken = updatePluginOptions?.GitHubToken;
                var downloadUrlPrefix = updatePluginOptions?.DownloadUrlPrefix;
                var updateChannel = string.IsNullOrWhiteSpace(updatePluginOptions?.UpdateChannel)
                    ? Options.GitHubOptions.UpdateChannelOption.Stable.ToString()
                    : updatePluginOptions.UpdateChannel;
                var currentVersionText = GetCurrentVersion();
                var embyVersion = Plugin.Instance?.AppHost?.ApplicationVersion ?? new Version(0, 0, 0, 0);

                logger.Info(
                    "开始检查插件更新：当前插件版本={0}，当前Emby版本={1}，更新频道={2}",
                    currentVersionText,
                    embyVersion,
                    updateChannel);

                var apiResult = await Plugin.ReleaseInfoService.RefreshAndSelectReleaseForChannelAsync(cancellationToken, updateChannel, githubToken).ConfigureAwait(false);
                if (apiResult == null)
                {
                    throw new Exception("未找到匹配当前更新频道的 Release");
                }

                var latestReleaseTag = string.IsNullOrWhiteSpace(apiResult?.tag_name)
                    ? "0.0.0.0"
                    : apiResult.tag_name.Trim();
                var compatibility = await FetchCompatibilityManifest(cancellationToken, updateChannel, apiResult.prerelease).ConfigureAwait(false);
                var (minVersion, maxVersion) = GetEmbyVersionRange(compatibility);
                logger.Info(
                    "版本信息：最新插件={0}，当前插件={1}，当前Emby={2}，兼容Emby版本区间=[{3},{4}]",
                    latestReleaseTag,
                    currentVersionText,
                    embyVersion,
                    minVersion?.ToString() ?? "*",
                    maxVersion?.ToString() ?? "*");

                if (string.Equals(currentVersionText, latestReleaseTag, StringComparison.OrdinalIgnoreCase))
                {
                    logger.Info("无需更新：目标 Tag 与当前插件版本一致，tag={0}", latestReleaseTag);
                    progress.Report(100);
                    return;
                }

                if (!IsEmbyVersionCompatible(compatibility, embyVersion, out var incompatibleReason))
                {
                    logger.Warn("跳过插件更新：{0}", incompatibleReason);
                    activityManager.Create(new ActivityLogEntry
                    {
                        Name = Plugin.Instance.Name + " update skipped on " + serverApplicationHost.FriendlyName,
                        Type = "PluginUpdateSkipped",
                        Overview = incompatibleReason,
                        Severity = LogSeverity.Info
                    });

                    progress.Report(100);
                    return;
                }

                logger.Info("版本校验通过：允许检查并更新插件。");
                var assets = apiResult?.assets ?? new List<ReleaseInfoService.ReleaseAssetInfo>();
                string targetAssetName = null;

                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    if (RuntimeInformation.OSArchitecture == Architecture.X64)
                    {
                        targetAssetName = "MediaInfoKeeper.win-x64.dll";
                    }
                    else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                    {
                        targetAssetName = "MediaInfoKeeper.win-arm64.dll";
                    }
                }
                else if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        if (RuntimeInformation.OSArchitecture == Architecture.X64)
                        {
                            targetAssetName = "MediaInfoKeeper.osx-x64.dll";
                        }
                        else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                        {
                            targetAssetName = "MediaInfoKeeper.osx-arm64.dll";
                        }
                    }
                    else
                    {
                        if (RuntimeInformation.OSArchitecture == Architecture.X64)
                        {
                            targetAssetName = "MediaInfoKeeper.linux-x64.dll";
                        }
                        else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                        {
                            targetAssetName = "MediaInfoKeeper.linux-arm64.dll";
                        }
                    }
                }

                logger.Info(
                    "插件更新资源选择：platform={0}, architecture={1}, preferredAsset={2}",
                    Environment.OSVersion.Platform,
                    RuntimeInformation.OSArchitecture,
                    targetAssetName ?? "(fallback-only)");

                var matchedAsset = string.IsNullOrWhiteSpace(targetAssetName)
                    ? null
                    : assets.FirstOrDefault(asset => string.Equals(asset.name, targetAssetName, StringComparison.Ordinal));
                var usedFallbackAsset = false;
                if (matchedAsset == null)
                {
                    matchedAsset = assets.FirstOrDefault(asset => string.Equals(asset.name, PluginAssemblyFilename, StringComparison.Ordinal));
                    usedFallbackAsset = matchedAsset != null;
                }

                if (matchedAsset == null)
                {
                    throw new Exception($"未找到适用于当前平台/架构的插件资源，preferred={targetAssetName ?? "(none)"}, fallback={PluginAssemblyFilename}");
                }

                if (usedFallbackAsset)
                {
                    logger.Warn(
                        "插件更新资源选择：未找到精确匹配，回退到默认全量包。preferred={0}, fallback={1}",
                        targetAssetName ?? "(none)",
                        PluginAssemblyFilename);
                }
                else
                {
                    logger.Info("插件更新资源选择：使用精确匹配资源 {0}", matchedAsset.name);
                }

                var url = matchedAsset.browser_download_url;
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    throw new Exception("下载地址无效");
                }

                var downloadUrl = ApplyUrlPrefix(url, downloadUrlPrefix);
                logger.Info("开始下载插件：版本={0}，下载地址={1}", latestReleaseTag, downloadUrl);

                var downloadRequestOptions = new HttpRequestOptions
                {
                    Url = downloadUrl,
                    CancellationToken = cancellationToken,
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    LogRequest = true,
                    LogResponse = true,
                    Progress = progress
                };
                if (!string.IsNullOrWhiteSpace(githubToken))
                {
                    downloadRequestOptions.RequestHeaders["Authorization"] = $"token {githubToken}";
                }

                using (var downloadResponse = await httpClient.GetResponse(downloadRequestOptions)
                           .ConfigureAwait(false))
                {
                    if ((int)downloadResponse.StatusCode < 200 || (int)downloadResponse.StatusCode >= 300)
                    {
                        using var reader = new StreamReader(downloadResponse.Content);
                        var responseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                        logger.Error("下载插件失败：status={0}, body={1}", (int)downloadResponse.StatusCode, responseBody);
                        throw new Exception($"下载插件失败: {(int)downloadResponse.StatusCode}");
                    }

                    using (var memoryStream = new MemoryStream())
                    {
                        await downloadResponse.Content.CopyToAsync(memoryStream, 81920, cancellationToken)
                            .ConfigureAwait(false);

                        memoryStream.Seek(0, SeekOrigin.Begin);
                        var dllFilePath = Path.Combine(applicationPaths.PluginsPath, PluginAssemblyFilename);

                        await using (var fileStream =
                                     new FileStream(dllFilePath, FileMode.Create, FileAccess.Write))
                        {
                            await memoryStream.CopyToAsync(fileStream, 81920, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }

                logger.Info(
                    "插件更新完成：版本={0}，重启后生效", latestReleaseTag);

                activityManager.Create(new ActivityLogEntry
                {
                    Name = Plugin.Instance.Name + " Updated to " + latestReleaseTag + " on " +
                           serverApplicationHost.FriendlyName,
                    Type = "PluginUpdateInstalled",
                    Severity = LogSeverity.Info
                });

                applicationHost.NotifyPendingRestart();
                if (updatePluginOptions?.RestartEmbyAfterUpdate == true)
                {
                    if (applicationHost.CanSelfRestart)
                    {
                        logger.Info("插件更新完成，配置已启用自动重启，正在触发 Emby 自重启。");
                        applicationHost.Restart();
                    }
                    else
                    {
                        logger.Warn("插件更新完成，但当前 Emby 环境不支持自重启，请手动重启服务。");
                    }
                }
            }
            catch (Exception ex)
            {
                activityManager.Create(new ActivityLogEntry
                {
                    Name = Plugin.Instance.Name + " update failed on " + serverApplicationHost.FriendlyName,
                    Type = "PluginUpdateFailed",
                    Overview = ex.Message,
                    Severity = LogSeverity.Error
                });

                logger.Error("插件更新失败：{0}", ex.Message);
                logger.Debug(ex.StackTrace);
            }

            progress.Report(100);
        }

        private async Task<PluginCompatibilityInfo> FetchCompatibilityManifest(
            CancellationToken cancellationToken,
            string updateChannel,
            bool isPrerelease)
        {
            try
            {
                var githubToken = Plugin.Instance.Options.GetEffectiveUpdatePluginOptions()?.GitHubToken;
                var manifestRequestOptions = new HttpRequestOptions
                {
                    Url = RepoVersionUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    LogRequest = true,
                    LogResponse = true
                };
                if (!string.IsNullOrWhiteSpace(githubToken))
                {
                    manifestRequestOptions.RequestHeaders["Authorization"] = $"token {githubToken}";
                }

                using var response = await httpClient.SendAsync(manifestRequestOptions, "GET").ConfigureAwait(false);
                string manifestResponseBody;
                await using (var stream = response.Content)
                using (var reader = new StreamReader(stream))
                {
                    manifestResponseBody = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                {
                    logger.Error("加载 Version.json 失败：status={0}, body={1}", (int)response.StatusCode, manifestResponseBody);
                    throw new Exception($"加载 Version.json 失败: {(int)response.StatusCode}");
                }

                var manifest = jsonSerializer.DeserializeFromString<PluginManifestInfo>(manifestResponseBody);
                var compatibility = SelectCompatibilityInfo(manifest, updateChannel, isPrerelease);
                if (compatibility != null)
                {
                    return compatibility;
                }
            }
            catch (Exception ex)
            {
                logger.Debug("加载 Version.json 失败：url={0}, error={1}", RepoVersionUrl, ex.Message);
            }

            logger.Info("未获取到 Version.json 兼容信息，默认允许更新。");
            return null;
        }

        private static PluginCompatibilityInfo SelectCompatibilityInfo(
            PluginManifestInfo manifest,
            string updateChannel,
            bool isPrerelease)
        {
            if (manifest == null)
            {
                return null;
            }

            var preferBeta = string.Equals(
                updateChannel,
                Options.GitHubOptions.UpdateChannelOption.Beta.ToString(),
                StringComparison.OrdinalIgnoreCase);

            if (preferBeta && isPrerelease)
            {
                return manifest.beta ?? manifest.latest;
            }

            return manifest.latest;
        }

        private static (Version minVersion, Version maxVersion) GetEmbyVersionRange(PluginCompatibilityInfo compatibility)
        {
            if (compatibility == null)
            {
                return (null, null);
            }

            var minVersion = ParseOptionalVersion(
                compatibility.minEmbyVersion ??
                compatibility.embyMinVersion ??
                compatibility.min_version);
            var maxVersion = ParseOptionalVersion(
                compatibility.maxEmbyVersion ??
                compatibility.embyMaxVersion ??
                compatibility.max_version);
            return (minVersion, maxVersion);
        }

        private static bool IsEmbyVersionCompatible(
            PluginCompatibilityInfo compatibility,
            Version currentEmbyVersion,
            out string reason)
        {
            reason = null;
            if (currentEmbyVersion == null)
            {
                currentEmbyVersion = new Version(0, 0, 0, 0);
            }

            if (compatibility == null)
            {
                return true;
            }

            var (minVersion, maxVersion) = GetEmbyVersionRange(compatibility);

            if (minVersion != null && currentEmbyVersion < minVersion)
            {
                reason = string.Format(
                    "当前 Emby 版本 {0} 低于插件要求的最小版本 {1}",
                    currentEmbyVersion,
                    minVersion);
                return false;
            }

            if (maxVersion != null && currentEmbyVersion > maxVersion)
            {
                reason = string.Format(
                    "当前 Emby 版本 {0} 高于插件支持的最大版本 {1}",
                    currentEmbyVersion,
                    maxVersion);
                return false;
            }

            return true;
        }

        private static string ApplyUrlPrefix(string url, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return url;
            }

            return $"{prefix.TrimEnd('/')}/{url.TrimStart('/')}";
        }

        private static Version ParseOptionalVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(1)
                : value;
            return Version.TryParse(normalized, out var version) ? version : null;
        }

        private static string GetCurrentVersion()
        {
            var releaseTag = GetAssemblyReleaseTag(Assembly.GetExecutingAssembly());
            if (!string.IsNullOrWhiteSpace(releaseTag))
            {
                return releaseTag;
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "0.0.0.0" : $"v{version.ToString(4)}";
        }

        private static string GetAssemblyReleaseTag(Assembly assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            var releaseTagAttribute = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attr => string.Equals(attr.Key, "ReleaseTag", StringComparison.Ordinal));

            return string.IsNullOrWhiteSpace(releaseTagAttribute?.Value) ? null : releaseTagAttribute.Value.Trim();
        }

        internal class PluginManifestInfo
        {
            public PluginCompatibilityInfo latest { get; set; }

            public PluginCompatibilityInfo beta { get; set; }
        }

        internal class PluginCompatibilityInfo
        {
            public string minEmbyVersion { get; set; }

            public string maxEmbyVersion { get; set; }

            public string embyMinVersion { get; set; }

            public string embyMaxVersion { get; set; }

            public string min_version { get; set; }

            public string max_version { get; set; }
        }

    }
}
