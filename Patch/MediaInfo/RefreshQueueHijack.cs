using System;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     接管 Emby 刷新队列入口，按刷新意图分流到插件 runner 执行并控制并发。
    /// </summary>
    public static class RefreshQueueHijack {
        private static Harmony harmony;
        private static MethodInfo queueRefresh;
        private static MethodInfo queueRefreshWithDequeue;
        private static ILogger logger;
        private static volatile bool configuredEnabled;

        /// <summary>
        ///     补丁是否已命中并安装到两个 QueueRefresh overload。
        /// </summary>
        public static bool IsReady => harmony != null &&
                                      queueRefresh != null &&
                                      queueRefreshWithDequeue != null;

        /// <summary>
        ///     初始化 QueueRefresh 劫持补丁。
        /// </summary>
        /// <param name="pluginLogger">插件日志。</param>
        /// <param name="enabled">是否启用补丁。</param>
        public static void Initialize(ILogger pluginLogger, bool enabled) {
            configuredEnabled = enabled;

            if (harmony != null) return;

            logger = pluginLogger;
            if (!enabled) return;

            try {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManagerType = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManagerType == null) {
                    PatchLog.InitFailed(logger, nameof(RefreshQueueHijack), "未找到 ProviderManager 类型");
                    return;
                }

                var assemblyVersion = embyProviders.GetName().Version;
                queueRefresh = ResolveMethod(
                    providerManagerType,
                    assemblyVersion,
                    "queue-refresh-exact",
                    "QueueRefresh",
                    new[] {
                        typeof(long),
                        typeof(MetadataRefreshOptions),
                        typeof(RefreshPriority)
                    },
                    "RefreshQueueHijack.QueueRefresh");
                queueRefreshWithDequeue = ResolveMethod(
                    providerManagerType,
                    assemblyVersion,
                    "queue-refresh-with-dequeue-exact",
                    "QueueRefresh",
                    new[] {
                        typeof(long),
                        typeof(MetadataRefreshOptions),
                        typeof(RefreshPriority),
                        typeof(bool)
                    },
                    "RefreshQueueHijack.QueueRefresh(dequeue)");

                if (queueRefresh == null || queueRefreshWithDequeue == null) {
                    PatchLog.InitFailed(logger, nameof(RefreshQueueHijack), "未命中 QueueRefresh 方法");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.refreshqueuehijack");
                PatchMethod(queueRefresh, nameof(QueueRefreshPrefix));
                PatchMethod(queueRefreshWithDequeue, nameof(QueueRefreshWithDequeuePrefix));
            }
            catch (Exception ex) {
                logger?.Error("RefreshQueueHijack patch 初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        /// <summary>
        ///     设置补丁运行状态。Harmony 安装后不卸载，运行时由前缀判断是否接管。
        /// </summary>
        /// <param name="enabled">是否启用。</param>
        public static void SetEnabled(bool enabled) {
            configuredEnabled = enabled;
        }

        private static MethodInfo ResolveMethod(
            Type providerManagerType,
            Version assemblyVersion,
            string profileName,
            string methodName,
            Type[] parameterTypes,
            string context) {
            return PatchMethodResolver.Resolve(
                providerManagerType,
                assemblyVersion,
                new MethodSignatureProfile {
                    Name = profileName,
                    MethodName = methodName,
                    BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    ParameterTypes = parameterTypes,
                    ReturnType = typeof(void)
                },
                logger,
                context);
        }

        private static void PatchMethod(MethodInfo method, string prefix) {
            PatchLog.Patched(logger, nameof(RefreshQueueHijack), method);
            harmony.Patch(method, new HarmonyMethod(typeof(RefreshQueueHijack), prefix));
        }

        private static bool QueueRefreshPrefix([HarmonyArgument(0)] long id,
            [HarmonyArgument(1)] MetadataRefreshOptions options, [HarmonyArgument(2)] RefreshPriority priority) {
            return QueueRefreshWithDequeuePrefix(id, options, priority, false);
        }

        private static bool QueueRefreshWithDequeuePrefix(
            [HarmonyArgument(0)] long id,
            [HarmonyArgument(1)] MetadataRefreshOptions options,
            [HarmonyArgument(2)] RefreshPriority priority,
            [HarmonyArgument(3)] bool dequeueIfAlreadyQueued) {
            var allowFfProcess = MetadataRefreshAllowFfProcess.HasCurrentAllowance;
            if (!ShouldTakeOverRefreshQueue(id, options, allowFfProcess)) return true;

            var runner = SelectRunner(id, options);
            if (runner == RefreshQueueHijackKind.MediaInfo)
                _ = MediaInfoRunner.ExtractMediaInfoAsync(
                    id,
                    "Emby刷新队列",
                    priority: priority,
                    replaceQueued: dequeueIfAlreadyQueued);
            else
                _ = MetaDataRunner.RefreshMetaDataAsync(
                    id,
                    options,
                    priority: priority,
                    replaceQueued: dequeueIfAlreadyQueued,
                    allowFfProcess: allowFfProcess);

            return false;
        }

        private static bool ShouldTakeOverRefreshQueue(
            long itemId,
            MetadataRefreshOptions options,
            bool allowFfProcess) {
            return itemId > 0 &&
                   options != null &&
                   (configuredEnabled || allowFfProcess) &&
                   Plugin.LibraryManager != null &&
                   Plugin.ProviderManager != null;
        }

        /// <summary>
        ///     判断当前 QueueRefresh 请求应该交给元数据 runner 还是媒体信息 runner。
        /// </summary>
        /// <remarks>
        ///     只有音视频条目且刷新选项呈现插件 MediaInfo-only 的 ValidationOnly 形态时，才分流到媒体信息 runner。
        ///     其他 ValidationOnly 请求可能是目录发现、库校验或多版本合并刷新，必须保守地交给元数据 runner。
        /// </remarks>
        private static RefreshQueueHijackKind SelectRunner(long itemId, MetadataRefreshOptions options) {
            if (!IsMediaInfoRefreshOptions(options)) return RefreshQueueHijackKind.Metadata;

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            return item is Video || item is Audio
                ? RefreshQueueHijackKind.MediaInfo
                : RefreshQueueHijackKind.Metadata;
        }

        /// <summary>
        ///     判断刷新选项是否符合插件媒体信息提取所使用的稳定特征。
        /// </summary>
        private static bool IsMediaInfoRefreshOptions(MetadataRefreshOptions options) {
            return options != null &&
                   options.EnableRemoteContentProbe &&
                   options.MetadataRefreshMode == MetadataRefreshMode.ValidationOnly &&
                   !options.ReplaceAllMetadata &&
                   options.ImageRefreshMode == MetadataRefreshMode.ValidationOnly &&
                   !options.ReplaceAllImages &&
                   !options.EnableThumbnailImageExtraction &&
                   !options.EnableSubtitleDownloading;
        }

        private enum RefreshQueueHijackKind {
            Metadata,
            MediaInfo
        }
    }
}
