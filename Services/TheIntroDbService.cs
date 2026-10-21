using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.Common;

namespace MediaInfoKeeper.Services {
    internal static class TheIntroDbService {
        private const string BaseUrl = "https://api.theintrodb.org/v4";
        private const string MarkerCacheScope = "theintrodb-markers";

        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly TimeSpan TheIntroDbSuccessCacheDuration = TimeSpan.FromHours(6);

        public static Task<MarkerLookupResult> GetMarkersAsync(Movie movie, CancellationToken cancellationToken) {
            if (movie == null) return Task.FromResult(NotFound("empty movie"));

            var query = BuildLookupQuery(movie, true);
            if (!query.IsValid) return Task.FromResult(NotFound(query.Reason));

            return GetMarkersAsync(query, movie, FormatItemForLog(movie), cancellationToken);
        }

        public static Task<MarkerLookupResult> GetMarkersAsync(Episode episode, CancellationToken cancellationToken) {
            if (episode == null) return Task.FromResult(NotFound("empty episode"));

            var query = BuildLookupQuery(episode, true);
            if (!query.IsValid) return Task.FromResult(NotFound(query.Reason));

            return GetMarkersAsync(query, episode, FormatItemForLog(episode), cancellationToken);
        }

        public static async Task<MarkerSubmitResult> SubmitMarkersAsync(BaseItem item,
            CancellationToken cancellationToken) {
            if (item == null) return SubmitSkipped("empty item");

            var apiKey = Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.TheIntroDb?.ApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) return SubmitSkipped("missing api key");

            if (!TryBuildSubmissionIdentity(item, out var tmdbId, out var tvdbId, out var mediaType, out var season, out var episode,
                    out var imdbId, out var reason))
                return SubmitSkipped(reason);

            var chapters = Plugin.IntroSkipChapterApi.GetChapters(item);
            var introStartTicks = GetSubmitMarkerTicks(chapters, MarkerType.IntroStart);
            var introEndTicks = GetSubmitMarkerTicks(chapters, MarkerType.IntroEnd);
            var creditsStartTicks = GetSubmitMarkerTicks(chapters, MarkerType.CreditsStart);
            var videoDurationMs = GetValidVideoDurationMs(item);
            var submittedSegments = 0;
            var skippedAny = false;
            MarkerSubmitResult lastFailure = null;

            if (TryBuildSegmentRequest(
                    item,
                    tmdbId,
                    tvdbId,
                    mediaType,
                    "intro",
                    season,
                    episode,
                    videoDurationMs,
                    TicksToMilliseconds(introStartTicks),
                    TicksToMilliseconds(introEndTicks),
                    imdbId,
                    out var introRequest,
                    out _)) {
                var introResult = await SubmitSegmentAsync(introRequest, item, cancellationToken).ConfigureAwait(false);
                if (introResult.Succeeded) {
                    submittedSegments += Math.Max(1, introResult.SubmittedSegments);
                }
                else if (introResult.Skipped) {
                    skippedAny = true;
                }
                else {
                    if (introResult.RateLimited) return introResult;

                    lastFailure = introResult;
                }
            }

            if (TryBuildSegmentRequest(
                    item,
                    tmdbId,
                    tvdbId,
                    mediaType,
                    "credits",
                    season,
                    episode,
                    videoDurationMs,
                    TicksToMilliseconds(creditsStartTicks),
                    null,
                    imdbId,
                    out var creditsRequest,
                    out _)) {
                var creditsResult =
                    await SubmitSegmentAsync(creditsRequest, item, cancellationToken).ConfigureAwait(false);
                if (creditsResult.Succeeded) {
                    submittedSegments += Math.Max(1, creditsResult.SubmittedSegments);
                }
                else if (creditsResult.Skipped) {
                    skippedAny = true;
                }
                else {
                    lastFailure = creditsResult;
                }
            }

            if (submittedSegments > 0) InvalidateMarkerCache(item);

            if (lastFailure != null) {
                lastFailure.SubmittedSegments = submittedSegments;
                return lastFailure;
            }

            if (submittedSegments > 0)
                return new MarkerSubmitResult {
                    Succeeded = true,
                    SubmittedSegments = submittedSegments
                };

            return skippedAny
                ? SubmitSkipped("all submitted segments skipped")
                : SubmitSkipped("no valid intro or credits markers");
        }

        internal static string FormatItemForLog(BaseItem item) {
            if (item is Episode episode) {
                var seriesName = episode.FindSeriesName();
                return
                    $"{(string.IsNullOrWhiteSpace(seriesName) ? "<unknown>" : seriesName.Trim())} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
            }

            return item?.FileName ?? item?.Path ?? item?.Name ?? "<unknown>";
        }

        private static async Task<MarkerLookupResult> GetMarkersAsync(
            MarkerLookupQuery query,
            BaseItem item,
            string detail,
            CancellationToken cancellationToken) {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null) return NotFound("IHttpClient unavailable");

            var apiUrl = BuildApiUrl("media");
            var apiKey = Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.TheIntroDb?.ApiKey?.Trim();
            var diskCachedResult = PluginDiskCache.GetJson<MarkerLookupResult>(
                MarkerCacheScope,
                query.CacheKey,
                TheIntroDbSuccessCacheDuration,
                JsonOptions);
            if (diskCachedResult != null) return NotFound("cache hit");

            var requestUrl = apiUrl + "?" + string.Join("&", query.RequestParts);
            try {
                var requestOptions = new HttpRequestOptions {
                    Url = requestUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    TimeoutMs = 10000,
                    CacheMode = CacheMode.None,
                    ThrowOnErrorResponse = false
                };

                if (!string.IsNullOrWhiteSpace(apiKey)) requestOptions.RequestHeaders["Authorization"] = "Bearer " + apiKey;

                using var response = await httpClient.SendAsync(requestOptions, "GET").ConfigureAwait(false);
                string body = null;
                if (response.Content != null) {
                    using var reader = new StreamReader(response.Content);
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                var statusCode = (int)response.StatusCode;
                if (statusCode == 404) {
                    var notFoundResult = NotFound("404 Not Found");
                    PluginDiskCache.SetJson(MarkerCacheScope, query.CacheKey, notFoundResult, JsonOptions);
                    return notFoundResult;
                }

                if (statusCode == 429) {
                    Plugin.SharedLogger?.Info("TheIntroDB 请求达到限制: {0}", detail);
                    return new MarkerLookupResult {
                        Found = false,
                        RateLimited = true,
                        Reason = "rate limited"
                    };
                }

                if (statusCode < 200 || statusCode >= 300) {
                    Plugin.SharedLogger?.Info("TheIntroDB 请求失败: status={0}, {1}, body={2}", statusCode, detail, body);
                    return NotFound("http " + statusCode);
                }

                var media = JsonSerializer.Deserialize<MediaResponse>(body, JsonOptions);
                var intro = SelectFirstValidIntro(media?.Intro);
                var credits = SelectFirstValidCredits(media?.Credits, item);
                if (intro == null && credits == null) {
                    var notFoundResult = NotFound("no usable segment");
                    PluginDiskCache.SetJson(MarkerCacheScope, query.CacheKey, notFoundResult, JsonOptions);
                    return notFoundResult;
                }

                var result = new MarkerLookupResult {
                    Found = true,
                    IntroStartTicks = intro?.StartTicks,
                    IntroEndTicks = intro?.EndTicks,
                    CreditsStartTicks = credits
                };
                PluginDiskCache.SetJson(MarkerCacheScope, query.CacheKey, result, JsonOptions);
                LogHit(detail, result);
                return result;
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Info("TheIntroDB 查询异常: {0}, {1}", detail, ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                return NotFound(ex.Message);
            }
        }

        private static async Task<MarkerSubmitResult> SubmitSegmentAsync(
            SubmissionRequest submission,
            BaseItem item,
            CancellationToken cancellationToken) {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null) return SubmitSkipped("IHttpClient unavailable");

            var apiKey = Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.TheIntroDb?.ApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey)) return SubmitSkipped("missing api key");

            var apiUrl = BuildApiUrl("submit");
            var detail = FormatItemForLog(item);
            var bodyJson = JsonSerializer.Serialize(submission, JsonOptions);

            try {
                var requestOptions = new HttpRequestOptions {
                    Url = apiUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    RequestContent = bodyJson.AsMemory(),
                    RequestContentType = "application/json",
                    UserAgent = "MediaInfoKeeper",
                    EnableDefaultUserAgent = false,
                    TimeoutMs = 10000,
                    ThrowOnErrorResponse = false
                };
                requestOptions.RequestHeaders["Authorization"] = "Bearer " + apiKey;

                using var response = await httpClient.SendAsync(requestOptions, "POST").ConfigureAwait(false);
                var responseBody = await ReadResponseBodyAsync(response).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;
                if (statusCode >= 200 && statusCode < 300) {
                    var result = JsonSerializer.Deserialize<SubmissionResponse>(responseBody, JsonOptions);
                    if (result?.Ok != true || result.Submissions == null || result.Submissions.Count == 0)
                        return new MarkerSubmitResult { Reason = "invalid submission response" };

                    Plugin.SharedLogger?.Info("TheIntroDB 上报成功: {0}, segment={1}", detail, submission.Segment);
                    return new MarkerSubmitResult {
                        Succeeded = true,
                        SubmittedSegments = result.Submissions.Count
                    };
                }

                if (statusCode == 409) {
                    Plugin.SharedLogger?.Info("TheIntroDB 上报跳过: {0}, segment={1}, 已存在或冲突, body={2}", detail,
                        submission.Segment, responseBody);
                    return SubmitSkipped("conflict");
                }

                if (statusCode == 429) {
                    Plugin.SharedLogger?.Info("TheIntroDB 上报达到限制: {0}, segment={1}, body={2}", detail,
                        submission.Segment, responseBody);
                    return new MarkerSubmitResult {
                        Succeeded = false,
                        RateLimited = true,
                        Reason = "rate limited"
                    };
                }

                Plugin.SharedLogger?.Info("TheIntroDB 上报失败: status={0}, {1}, segment={2}, body={3}", statusCode, detail,
                    submission.Segment, responseBody);
                return new MarkerSubmitResult {
                    Succeeded = false,
                    Reason = "http " + statusCode
                };
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Info("TheIntroDB 上报异常: {0}, segment={1}, {2}", detail, submission.Segment,
                    ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                return new MarkerSubmitResult {
                    Succeeded = false,
                    Reason = ex.Message
                };
            }
        }

        private static async Task<string> ReadResponseBodyAsync(HttpResponseInfo response) {
            if (response?.Content == null) return null;

            using var reader = new StreamReader(response.Content);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static bool TryBuildSubmissionIdentity(
            BaseItem item,
            out int? tmdbId,
            out int? tvdbId,
            out string mediaType,
            out int? season,
            out int? episode,
            out string imdbId,
            out string reason) {
            tmdbId = null;
            tvdbId = null;
            mediaType = null;
            season = null;
            episode = null;
            imdbId = null;
            reason = null;

            if (item is Movie movie) {
                if (TryParsePositiveInt(movie.GetProviderId(MetadataProviders.Tvdb), out var movieTvdbId))
                    tvdbId = movieTvdbId;
                else if (TryParsePositiveInt(movie.GetProviderId(MetadataProviders.Tmdb), out var movieTmdbId))
                    tmdbId = movieTmdbId;
                imdbId = NormalizeImdbId(movie.GetProviderId(MetadataProviders.Imdb));
                if (!tvdbId.HasValue && !tmdbId.HasValue && imdbId == null) {
                    reason = "missing movie ids";
                    return false;
                }

                mediaType = "movie";
                return true;
            }

            if (item is Episode episodeItem) {
                if (TryParsePositiveInt(episodeItem.Series?.GetProviderId(MetadataProviders.Tvdb.ToString()), out var seriesTvdbId))
                    tvdbId = seriesTvdbId;
                else if (TryParsePositiveInt(episodeItem.Series?.GetProviderId(MetadataProviders.Tmdb.ToString()), out var seriesTmdbId))
                    tmdbId = seriesTmdbId;
                imdbId = NormalizeImdbId(episodeItem.Series?.GetProviderId(MetadataProviders.Imdb.ToString()));
                if (!tvdbId.HasValue && !tmdbId.HasValue && imdbId == null) {
                    reason = "missing series ids";
                    return false;
                }

                if (!episodeItem.ParentIndexNumber.HasValue || !episodeItem.IndexNumber.HasValue) {
                    reason = "missing season or episode number";
                    return false;
                }

                mediaType = "tv";
                season = episodeItem.ParentIndexNumber.Value;
                episode = episodeItem.IndexNumber.Value;
                return true;
            }

            reason = "unsupported item type";
            return false;
        }

        private static long? GetSubmitMarkerTicks(IEnumerable<ChapterInfo> chapters, MarkerType markerType) {
            if (chapters == null) return null;

            foreach (var chapter in chapters) {
                if (chapter?.MarkerType != markerType || IntroDbMarkerSource.IsProviderMarker(chapter)) continue;

                return chapter.StartPositionTicks;
            }

            return null;
        }

        private static bool TryBuildSegmentRequest(
            BaseItem item,
            int? tmdbId,
            int? tvdbId,
            string mediaType,
            string segment,
            int? season,
            int? episode,
            long? videoDurationMs,
            long? startMs,
            long? endMs,
            string imdbId,
            out SubmissionRequest request,
            out string reason) {
            request = null;
            reason = null;

            if (!IsValidSegment(item, segment, startMs, endMs, out reason)) return false;

            request = new SubmissionRequest {
                TmdbId = tmdbId,
                TvdbId = tvdbId,
                Type = mediaType,
                Segment = segment,
                Season = season,
                Episode = episode,
                Mapping = item is Episode episodeItem ? GetEpisodeMapping(episodeItem) : null,
                VideoDurationMs = videoDurationMs,
                StartMs = startMs,
                EndMs = endMs,
                ImdbId = imdbId
            };
            return true;
        }

        private static bool IsValidSegment(BaseItem item, string segment, long? startMs, long? endMs, out string reason) {
            reason = null;
            if (string.Equals(segment, "intro", StringComparison.Ordinal)) {
                if (!endMs.HasValue || endMs.Value <= 0) {
                    reason = "missing intro end";
                    return false;
                }

                var introStartMs = Math.Max(0, startMs ?? 0);
                var durationMs = endMs.Value - introStartMs;
                if (durationMs < 5000 || durationMs > 200000) {
                    reason = "intro duration out of range";
                    return false;
                }

                return IsTimestampWithinMedia(item, introStartMs, endMs.Value, out reason);
            }

            if (string.Equals(segment, "credits", StringComparison.Ordinal)) {
                if (!startMs.HasValue || startMs.Value <= 0) {
                    reason = "missing credits start";
                    return false;
                }

                if (endMs.HasValue && endMs.Value <= startMs.Value) {
                    reason = "invalid credits range";
                    return false;
                }

                if (item?.RunTimeTicks.HasValue == true &&
                    startMs.Value >= item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond) {
                    reason = "credits start outside runtime";
                    return false;
                }

                return IsTimestampWithinMedia(item, startMs.Value, endMs, out reason);
            }

            reason = "unsupported segment";
            return false;
        }

        private static bool IsTimestampWithinMedia(BaseItem item, long startMs, long? endMs, out string reason) {
            reason = null;
            if (startMs > 21600000 || endMs > 21600000) {
                reason = "timestamp exceeds api limit";
                return false;
            }

            var runtimeMs = item?.RunTimeTicks / TimeSpan.TicksPerMillisecond;
            if (runtimeMs.HasValue && (startMs >= runtimeMs.Value || (endMs.HasValue && endMs.Value > runtimeMs.Value))) {
                reason = "timestamp outside runtime";
                return false;
            }

            return true;
        }

        private static long? GetValidVideoDurationMs(BaseItem item) {
            if (item?.RunTimeTicks.HasValue != true) return null;

            var durationMs = item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond;
            return durationMs == 0 || (durationMs >= 300000 && durationMs <= 21600000) ? durationMs : null;
        }

        private static long? TicksToMilliseconds(long? ticks) {
            return ticks.HasValue ? ticks.Value / TimeSpan.TicksPerMillisecond : null;
        }

        private static string NormalizeImdbId(string imdbId) {
            imdbId = imdbId?.Trim();
            if (string.IsNullOrWhiteSpace(imdbId) ||
                imdbId.Length < 9 ||
                imdbId.Length > 10 ||
                !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                return null;

            for (var i = 2; i < imdbId.Length; i++)
                if (!char.IsDigit(imdbId[i]))
                    return null;

            return imdbId.ToLowerInvariant();
        }

        private static List<string> BuildIdQueryParts(string tmdbId, string tvdbId, string imdbId) {
            var queryParts = new List<string>();
            if (TryParsePositiveInt(tvdbId, out var parsedTvdbId))
                queryParts.Add("tvdb_id=" + parsedTvdbId);
            else if (TryParsePositiveInt(tmdbId, out var parsedTmdbId))
                queryParts.Add("tmdb_id=" + parsedTmdbId);
            else if (!string.IsNullOrWhiteSpace(imdbId)) queryParts.Add("imdb_id=" + Uri.EscapeDataString(imdbId.Trim()));

            return queryParts;
        }

        private static MarkerLookupQuery BuildLookupQuery(BaseItem item, bool includeDuration) {
            var query = new MarkerLookupQuery();
            if (item is Movie movie) {
                var requestParts = BuildIdQueryParts(movie.GetProviderId(MetadataProviders.Tmdb), movie.GetProviderId(MetadataProviders.Tvdb),
                    movie.GetProviderId(MetadataProviders.Imdb));
                if (requestParts.Count == 0) {
                    query.Reason = "missing movie ids";
                    return query;
                }

                query.CacheKey = string.Join("&", requestParts);
                AppendDuration(requestParts, movie, includeDuration);
                query.RequestParts = requestParts;
                return query;
            }

            if (item is Episode episode) {
                var requestParts = BuildIdQueryParts(
                    episode.Series?.GetProviderId(MetadataProviders.Tmdb.ToString())?.Trim(),
                    episode.Series?.GetProviderId(MetadataProviders.Tvdb.ToString())?.Trim(),
                    episode.Series?.GetProviderId(MetadataProviders.Imdb.ToString())?.Trim());
                if (requestParts.Count == 0 || !episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue) {
                    query.Reason = "missing series ids or episode numbers";
                    return query;
                }

                requestParts.Add("season=" + episode.ParentIndexNumber.Value);
                requestParts.Add("episode=" + episode.IndexNumber.Value);
                requestParts.Add("mapping=" + GetEpisodeMapping(episode));
                query.CacheKey = string.Join("&", requestParts);
                AppendDuration(requestParts, episode, includeDuration);
                query.RequestParts = requestParts;
                return query;
            }

            query.Reason = "unsupported item type";
            return query;
        }

        private static void AppendDuration(List<string> queryParts, BaseItem item, bool includeDuration) {
            if (includeDuration && item?.RunTimeTicks.HasValue == true && item.RunTimeTicks.Value > 0)
                queryParts.Add("duration_ms=" + item.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond);
        }

        private static string BuildApiUrl(string endpoint) {
            return BaseUrl + "/" + endpoint;
        }

        private static string GetEpisodeMapping(Episode episode) {
            return episode.Series?.DisplayOrder switch {
                SeriesDisplayOrder.Aired => "official",
                SeriesDisplayOrder.Dvd => "dvd",
                SeriesDisplayOrder.Absolute => "absolute",
                _ => "default"
            };
        }

        private static void InvalidateMarkerCache(BaseItem item) {
            var query = BuildLookupQuery(item, false);
            if (!query.IsValid) return;

            PluginDiskCache.Remove(MarkerCacheScope, query.CacheKey, ".json");
            Plugin.SharedLogger?.Debug("TheIntroDB 查询缓存已失效: {0}", FormatItemForLog(item));
        }

        private static IntroSegment SelectFirstValidIntro(IEnumerable<SegmentTimestamp> segments) {
            if (segments == null) return null;

            foreach (var segment in segments) {
                if (!segment.End_Ms.HasValue || segment.End_Ms.Value <= 0) continue;

                var startMs = Math.Max(0, segment.Start_Ms ?? 0);
                if (segment.End_Ms.Value <= startMs) continue;

                return new IntroSegment {
                    StartTicks = startMs * TimeSpan.TicksPerMillisecond,
                    EndTicks = segment.End_Ms.Value * TimeSpan.TicksPerMillisecond
                };
            }

            return null;
        }

        private static long? SelectFirstValidCredits(IEnumerable<SegmentTimestamp> segments, BaseItem item) {
            if (segments == null) return null;

            foreach (var segment in segments) {
                if (!segment.Start_Ms.HasValue || segment.Start_Ms.Value <= 0) continue;

                var startTicks = segment.Start_Ms.Value * TimeSpan.TicksPerMillisecond;
                if (item?.RunTimeTicks.HasValue == true && startTicks >= item.RunTimeTicks.Value) continue;

                return startTicks;
            }

            return null;
        }

        private static void LogHit(string detail, MarkerLookupResult result) {
            if (result?.Found != true) return;

            Plugin.SharedLogger?.Info(
                "TheIntroDB 命中: {0} intro={1}-{2}, creditsStart={3}",
                detail,
                FormatTicks(result.IntroStartTicks),
                FormatTicks(result.IntroEndTicks),
                FormatTicks(result.CreditsStartTicks));
        }

        private static bool TryParsePositiveInt(string value, out int number) {
            return int.TryParse(value, out number) && number > 0;
        }

        private static string FormatTicks(long? ticks) {
            return ticks.HasValue ? new TimeSpan(ticks.Value).ToString(@"hh\:mm\:ss\.fff") : "<none>";
        }

        private static MarkerLookupResult NotFound(string reason) {
            return new MarkerLookupResult {
                Found = false,
                Reason = reason
            };
        }

        private static MarkerSubmitResult SubmitSkipped(string reason) {
            return new MarkerSubmitResult {
                Succeeded = false,
                Skipped = true,
                Reason = reason
            };
        }

        internal sealed class MarkerLookupResult {
            public bool Found { get; set; }

            public bool RateLimited { get; set; }

            public string Reason { get; set; }

            public long? IntroStartTicks { get; set; }

            public long? IntroEndTicks { get; set; }

            public long? CreditsStartTicks { get; set; }
        }

        internal sealed class MarkerSubmitResult {
            public bool Succeeded { get; set; }

            public bool Skipped { get; set; }

            public bool RateLimited { get; set; }

            public string Reason { get; set; }

            public int SubmittedSegments { get; set; }
        }

        private sealed class MediaResponse {
            public List<SegmentTimestamp> Intro { get; set; }

            public List<SegmentTimestamp> Credits { get; set; }
        }

        private sealed class SubmissionRequest {
            [JsonPropertyName("tmdb_id")] public int? TmdbId { get; set; }

            [JsonPropertyName("tvdb_id")] public int? TvdbId { get; set; }

            [JsonPropertyName("type")] public string Type { get; set; }

            [JsonPropertyName("segment")] public string Segment { get; set; }

            [JsonPropertyName("season")] public int? Season { get; set; }

            [JsonPropertyName("episode")] public int? Episode { get; set; }

            [JsonPropertyName("mapping")] public string Mapping { get; set; }

            [JsonPropertyName("video_duration_ms")]
            public long? VideoDurationMs { get; set; }

            [JsonPropertyName("start_ms")] public long? StartMs { get; set; }

            [JsonPropertyName("end_ms")] public long? EndMs { get; set; }

            [JsonPropertyName("imdb_id")] public string ImdbId { get; set; }
        }

        private sealed class SubmissionResponse {
            public bool Ok { get; set; }

            public List<JsonElement> Submissions { get; set; }
        }

        private sealed class SegmentTimestamp {
            public long? Start_Ms { get; set; }

            public long? End_Ms { get; set; }
        }

        private sealed class IntroSegment {
            public long StartTicks { get; set; }

            public long EndTicks { get; set; }
        }

        private sealed class MarkerLookupQuery {
            public List<string> RequestParts { get; set; }

            public string CacheKey { get; set; }

            public string Reason { get; set; }

            public bool IsValid => RequestParts != null && RequestParts.Count > 0 && string.IsNullOrWhiteSpace(Reason);
        }
    }
}
