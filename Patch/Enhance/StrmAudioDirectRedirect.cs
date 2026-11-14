using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     对远端 http(s) strm 音乐在原始静态取流阶段按条件改为 302，让客户端直接拉取。
    /// </summary>
    internal static class StrmAudioDirectRedirect {
        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo processRequestMethod;
        private static MethodInfo getStateMethod;
        private static MethodInfo disposeStateMethod;
        private static Type streamRequestType;
        private static Type streamStateType;
        private static Type progressiveAudioRequestType;
        private static bool followRedirect302 = true;
        private static string[] clientBlacklist = Array.Empty<string>();

        public static bool IsReady => harmony != null
                                      && processRequestMethod != null
                                      && getStateMethod != null
                                      && disposeStateMethod != null
                                      && (!isEnabled || isPatched);

        public static void Initialize(
            ILogger pluginLogger,
            bool enabled,
            bool follow302,
            string clientBlacklistText) {
            if (harmony != null) {
                Configure(enabled, follow302, clientBlacklistText);
                return;
            }

            logger = pluginLogger;
            isEnabled = enabled;
            ApplySettings(follow302, clientBlacklistText);

            try {
                var mediaEncoding = Assembly.Load("Emby.Server.MediaEncoding");
                var mediaEncodingVersion = mediaEncoding?.GetName().Version;
                var baseProgressiveStreamingServiceType =
                    mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.Progressive.BaseProgressiveStreamingService",
                        false);
                var baseStreamingServiceType =
                    mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.BaseStreamingService", false);
                streamRequestType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.StreamRequest", false);
                streamStateType = mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.StreamState", false);
                progressiveAudioRequestType =
                    mediaEncoding?.GetType("Emby.Server.MediaEncoding.Api.Progressive.GetProgressiveAudioStream",
                        false);

                if (baseProgressiveStreamingServiceType == null ||
                    baseStreamingServiceType == null ||
                    streamRequestType == null ||
                    streamStateType == null ||
                    progressiveAudioRequestType == null) {
                    PatchLog.InitFailed(logger, nameof(StrmAudioDirectRedirect), "未找到音乐播放流相关类型");
                    return;
                }

                processRequestMethod = PatchMethodResolver.Resolve(
                    baseProgressiveStreamingServiceType,
                    mediaEncodingVersion,
                    new MethodSignatureProfile {
                        Name = "baseprogressivestreamingservice-processrequest-exact-audio",
                        MethodName = "ProcessRequest",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            streamRequestType,
                            typeof(bool)
                        },
                        ReturnType = typeof(Task<object>)
                    },
                    logger,
                    "StrmAudioDirectRedirect.BaseProgressiveStreamingService.ProcessRequest");

                getStateMethod = PatchMethodResolver.Resolve(
                    baseStreamingServiceType,
                    mediaEncodingVersion,
                    new MethodSignatureProfile {
                        Name = "basestreamingservice-getstate-exact-audio",
                        MethodName = "GetState",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            streamRequestType,
                            typeof(bool),
                            typeof(CancellationToken)
                        },
                        ReturnType = typeof(Task<>).MakeGenericType(streamStateType)
                    },
                    logger,
                    "StrmAudioDirectRedirect.BaseStreamingService.GetState");

                disposeStateMethod = streamStateType.GetMethod(
                    "Dispose",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] {
                        typeof(bool),
                        typeof(bool)
                    },
                    null);

                if (processRequestMethod == null || getStateMethod == null || disposeStateMethod == null) {
                    PatchLog.InitFailed(logger, nameof(StrmAudioDirectRedirect),
                        "未命中 ProcessRequest/GetState/StreamState.Dispose");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.strm-audio-direct-redirect");
                if (isEnabled) Patch();
            }
            catch (Exception ex) {
                PatchLog.InitFailed(logger, nameof(StrmAudioDirectRedirect), ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                processRequestMethod = null;
                getStateMethod = null;
                disposeStateMethod = null;
                streamRequestType = null;
                streamStateType = null;
                progressiveAudioRequestType = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enabled, bool follow302, string clientBlacklistText) {
            isEnabled = enabled;
            ApplySettings(follow302, clientBlacklistText);
            if (harmony == null) return;

            if (isEnabled)
                Patch();
            else
                Unpatch();
        }

        private static void Patch() {
            if (isPatched || harmony == null || processRequestMethod == null) return;

            harmony.Patch(
                processRequestMethod,
                new HarmonyMethod(typeof(StrmAudioDirectRedirect), nameof(ProcessRequestPrefix)));

            PatchLog.Patched(logger, nameof(StrmAudioDirectRedirect), processRequestMethod);
            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null || processRequestMethod == null) return;

            harmony.Unpatch(processRequestMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool ProcessRequestPrefix(
            object __instance,
            [HarmonyArgument(0)] object request,
            ref Task<object> __result) {
            var itemId = GetPropertyValue<string>(request, "Id");

            if (!isEnabled || __instance == null || request == null || progressiveAudioRequestType == null) return true;

            if (!progressiveAudioRequestType.IsInstanceOfType(request)) return true;

            if (!ResolveStrmPath(request)) return true;

            try {
                NormalizeOriginalRequest(request);
                if (!GetPropertyValue<bool>(request, "Static")) return true;

                var state = GetState(__instance, request);
                if (!CanRedirect(state)) {
                    DisposeState(state);
                    return true;
                }

                var resultFactory = GetPropertyValue<IHttpResultFactory>(__instance, "ResultFactory");
                var requestContext = GetPropertyValue<IRequest>(__instance, "Request");
                var mediaSource = GetPropertyValue<MediaSourceInfo>(state, "MediaSource");
                if (resultFactory == null || mediaSource == null) {
                    DisposeState(state);
                    return true;
                }

                if (IsClientBlocked(requestContext)) {
                    DisposeState(state);
                    return true;
                }

                var originalUrl = mediaSource.Path;
                var redirectUrl = ResolveRedirectUrl(originalUrl, requestContext?.UserAgent);
                __result = Task.FromResult(resultFactory.GetRedirectResult(redirectUrl));
                logger?.Info(
                    "StrmAudioDirectRedirect : itemId={0}, finalUrl={1}",
                    itemId,
                    redirectUrl);
                DisposeState(state);
                return false;
            }
            catch (Exception ex) {
                logger?.Warn(
                    "StrmAudioDirectRedirect 预判失败，回退 Emby 中转: itemId={0}, error={1}",
                    itemId,
                    ex.Message);
                return true;
            }
        }

        private static object GetState(object service, object request) {
            var requestContext = GetPropertyValue<IRequest>(service, "Request");
            if (requestContext == null) throw new InvalidOperationException("当前请求上下文为空");

            var task = getStateMethod?.Invoke(service,
                new[] { request, true, requestContext.CancellationToken }) as Task;
            if (task == null) throw new InvalidOperationException("GetState 返回为空");

            task.GetAwaiter().GetResult();
            var state = task.GetType()
                .GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(task);
            if (state == null) throw new InvalidOperationException("GetState 未返回有效 StreamState");

            return state;
        }

        private static bool ResolveStrmPath(object request) {
            var itemId = GetPropertyValue<string>(request, "Id");
            if (string.IsNullOrWhiteSpace(itemId)) return false;

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            var path = item?.Path ?? item?.FileName;
            return LibraryService.IsFileShortcut(path);
        }

        private static void NormalizeOriginalRequest(object request) {
            var streamFileName = GetPropertyValue<string>(request, "StreamFileName");
            if (string.IsNullOrEmpty(streamFileName)) return;

            if (string.Equals(Path.GetFileNameWithoutExtension(streamFileName), "original",
                    StringComparison.OrdinalIgnoreCase))
                SetPropertyValue(request, "Static", true);

            var container = GetPropertyValue<string>(request, "Container");
            if (!string.IsNullOrEmpty(container)) return;

            var extension = Path.GetExtension(streamFileName);
            if (string.IsNullOrEmpty(extension)) return;

            container = extension.TrimStart('.').ToLowerInvariant();
            SetPropertyValue(request, "Container", string.IsNullOrEmpty(container) ? null : container);
        }

        private static bool CanRedirect(object state) {
            var mediaSource = GetPropertyValue<MediaSourceInfo>(state, "MediaSource");
            if (state == null ||
                GetPropertyValue<bool>(state, "IsVideoRequest") ||
                GetPropertyValue<object>(state, "LiveStream") != null ||
                mediaSource == null)
                return false;

            if (!mediaSource.IsRemote ||
                mediaSource.Protocol != MediaProtocol.Http ||
                !mediaSource.SupportsDirectPlay ||
                mediaSource.IsInfiniteStream ||
                mediaSource.RequiresOpening ||
                mediaSource.RequiresClosing)
                return false;

            if (mediaSource.RequiredHttpHeaders != null && mediaSource.RequiredHttpHeaders.Count > 0) return false;

            return Uri.TryCreate(mediaSource.Path, UriKind.Absolute, out var uri) &&
                   (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private static void DisposeState(object state) {
            disposeStateMethod?.Invoke(state, new object[] { true, true });
        }

        private static void ApplySettings(bool follow302, string clientBlacklistText) {
            followRedirect302 = follow302;
            clientBlacklist = ParseClientBlacklist(clientBlacklistText);
        }

        private static string ResolveRedirectUrl(string url, string userAgent) {
            var normalizedUrl = NormalizeRedirectUrl(url);
            if (!followRedirect302) return normalizedUrl;

            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null || string.IsNullOrWhiteSpace(normalizedUrl)) return normalizedUrl;

            string resolvedUrl = null;
            foreach (var method in new[] { "GET", "HEAD" })
                try {
                    using var response = httpClient.SendAsync(
                        new HttpRequestOptions {
                            Url = normalizedUrl,
                            UserAgent = userAgent ?? string.Empty,
                            TimeoutMs = 3000,
                            BufferContent = false,
                            LogErrors = false,
                            LogRequest = false,
                            LogResponse = false,
                            EnableHttpCompression = false,
                            EnableKeepAlive = false,
                            EnableDefaultUserAgent = false,
                            ThrowOnErrorResponse = false
                        },
                        method).GetAwaiter().GetResult();

                    if (!string.IsNullOrWhiteSpace(response?.ResponseUrl)) {
                        resolvedUrl = NormalizeRedirectUrl(response.ResponseUrl);
                        break;
                    }
                }
                catch {
                }

            return string.IsNullOrWhiteSpace(resolvedUrl) ? normalizedUrl : resolvedUrl;
        }

        /// <summary>
        ///     按 IRI 转 URI 的安全字符白名单转义，保留已有百分号编码和签名查询串。
        ///     与 Django iri_to_uri 一样保留 URL 分隔符及 %，不解码或重建查询参数。
        /// </summary>
        private static string NormalizeRedirectUrl(string url) {
            if (string.IsNullOrWhiteSpace(url)) return url;

            url = url.Trim();
            StringBuilder result = null;
            var index = 0;
            while (index < url.Length) {
                if (IsSafeUrlCharacter(url[index])) {
                    result?.Append(url[index]);
                    index++;
                    continue;
                }

                // 仅遇到需要转义的字符时分配，并一次编码连续片段，保持代理项对完整。
                result ??= new StringBuilder(url.Length).Append(url, 0, index);
                var start = index++;
                while (index < url.Length && !IsSafeUrlCharacter(url[index])) index++;
                result.Append(Uri.EscapeDataString(url.Substring(start, index - start)));
            }

            return result?.ToString() ?? url;

            static bool IsSafeUrlCharacter(char value) {
                return value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                       || "-._~:/?#[]@!$&'()*+,;=%".IndexOf(value) >= 0;
            }
        }

        private static bool IsClientBlocked(IRequest requestContext) {
            if (requestContext == null || clientBlacklist.Length == 0) return false;

            var client = ResolveClient(requestContext);
            if (string.IsNullOrWhiteSpace(client)) return false;

            if (clientBlacklist.Any(pattern =>
                    client.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)) {
                logger?.Info(
                    "StrmAudioDirectRedirect: 客户端命中黑名单，回退 Emby 中转。client={0}",
                    client);
                return true;
            }

            return false;
        }

        private static string ResolveClient(IRequest requestContext) {
            var sessionContext = Plugin.Instance?.AppHost?.Resolve<ISessionContext>();
            var session = sessionContext?.GetSession(requestContext);
            return session?.Client?.Trim();
        }

        private static string[] ParseClientBlacklist(string text) {
            return string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : text
                    .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item?.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }

        private static T GetPropertyValue<T>(object instance, string propertyName) {
            if (instance == null) return default;

            var property = instance.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) return default;

            var value = property.GetValue(instance);
            if (value is T typedValue) return typedValue;

            return default;
        }

        private static void SetPropertyValue(object instance, string propertyName, object value) {
            var property = instance?.GetType().GetProperty(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.CanWrite == true) property.SetValue(instance, value);
        }
    }
}
