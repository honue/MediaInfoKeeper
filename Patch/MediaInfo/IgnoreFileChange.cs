using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     已有主媒体流的 STRM 不因普通刷新重新执行 FFProbeProvider。
    ///     外挂流单独扫描；刷新成功后确认文件修改时间，避免重复触发变更。
    /// </summary>
    internal static class IgnoreFileChange {
        private static Harmony harmony;
        private static MethodInfo canRefresh;
        private static MethodInfo refreshSingleItem;
        private static Type ffProbeProviderType;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && canRefresh != null && refreshSingleItem != null &&
                                      (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enabled) {
            logger = pluginLogger;
            isEnabled = enabled;
            if (harmony != null) {
                Configure(enabled);
                return;
            }

            try {
                var assembly = Assembly.Load("Emby.Providers");
                var managerType = assembly.GetType("Emby.Providers.Manager.ProviderManager");
                ffProbeProviderType = assembly.GetType("Emby.Providers.MediaInfo.FFProbeProvider");
                if (managerType == null || ffProbeProviderType == null) {
                    PatchLog.InitFailed(logger, nameof(IgnoreFileChange), "未找到 ProviderManager 或 FFProbeProvider 类型");
                    return;
                }

                canRefresh = Resolve(managerType, assembly.GetName().Version, "CanRefresh",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    new[] { typeof(IMetadataProvider), typeof(BaseItem), typeof(LibraryOptions),
                        typeof(bool), typeof(bool), typeof(bool) }, typeof(bool));
                refreshSingleItem = Resolve(managerType, assembly.GetName().Version, "RefreshSingleItem",
                    BindingFlags.Instance | BindingFlags.Public,
                    new[] { typeof(BaseItem), typeof(MetadataRefreshOptions), typeof(BaseItem[]),
                        typeof(LibraryOptions), typeof(CancellationToken) }, typeof(Task<ItemUpdateType>));
                if (canRefresh == null || refreshSingleItem == null) {
                    PatchLog.InitFailed(logger, nameof(IgnoreFileChange), "未命中完整的刷新入口签名");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.ignore-file-change");
                Configure(enabled);
            }
            catch (Exception ex) {
                harmony?.UnpatchAll(harmony.Id);
                isPatched = false;
                PatchLog.InitFailed(logger, nameof(IgnoreFileChange), ex.ToString());
            }
        }

        private static MethodInfo Resolve(Type type, Version version, string name, BindingFlags flags,
            Type[] parameters, Type returnType) {
            return PatchMethodResolver.Resolve(type, version, new MethodSignatureProfile {
                Name = "ignore-file-change-" + name,
                MethodName = name,
                BindingFlags = flags,
                ParameterTypes = parameters,
                ReturnType = returnType
            }, logger, "IgnoreFileChange." + name);
        }

        public static void Configure(bool enabled) {
            isEnabled = enabled;
            if (harmony == null || canRefresh == null || refreshSingleItem == null) return;
            if (!enabled) {
                harmony.UnpatchAll(harmony.Id);
                isPatched = false;
                return;
            }
            if (isPatched) return;

            // 先安装刷新收尾，再安装提供器过滤；部分安装失败时一并回滚。
            try {
                harmony.Patch(refreshSingleItem,
                    new HarmonyMethod(typeof(IgnoreFileChange), nameof(RefreshPrefix)),
                    new HarmonyMethod(typeof(IgnoreFileChange), nameof(RefreshPostfix)));
                harmony.Patch(canRefresh, new HarmonyMethod(typeof(IgnoreFileChange), nameof(CanRefreshPrefix)));
                isPatched = true;
                PatchLog.Patched(logger, nameof(IgnoreFileChange), refreshSingleItem);
                PatchLog.Patched(logger, nameof(IgnoreFileChange), canRefresh);
            }
            catch {
                harmony.UnpatchAll(harmony.Id);
                throw;
            }
        }

        private static bool ShouldIgnore(BaseItem item) {
            if (!isEnabled || item == null || item.InternalId == 0 || !item.IsFileProtocol ||
                !LibraryService.IsFileShortcut(item.Path) || FfProcessGuard.HasExplicitAllowance()) return false;

            // 外挂音轨不能代替主媒体信息；没有主流的条目仍走原生首次提取流程。
            return item.GetMediaStreams().Any(stream => stream != null && !stream.IsExternal &&
                (stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio));
        }

        private static bool CanRefreshPrefix([HarmonyArgument(0)] IMetadataProvider provider,
            [HarmonyArgument(1)] BaseItem item, ref bool __result) {
            if (!ffProbeProviderType.IsInstanceOfType(provider) || !ShouldIgnore(item)) return true;
            __result = false;
            return false;
        }

        private static void RefreshPrefix([HarmonyArgument(0)] BaseItem item,
            [HarmonyArgument(1)] MetadataRefreshOptions options, out RefreshState __state) {
            __state = null;
            if (!ShouldIgnore(item) || options?.DirectoryService == null) return;
            __state = new RefreshState { OriginalDateModified = item.DateModified };
            try {
                var file = options.DirectoryService.GetFile(item.Path);
                __state.FileDateModified = file?.LastWriteTimeUtc;
            }
            catch (Exception ex) {
                logger?.Warn($"忽略文件变更：读取文件时间失败 {item.Path}: {ex.Message}");
            }
        }

        private static void RefreshPostfix([HarmonyArgument(0)] BaseItem item,
            [HarmonyArgument(1)] MetadataRefreshOptions options,
            [HarmonyArgument(4)] CancellationToken cancellationToken,
            ref Task<ItemUpdateType> __result, RefreshState __state) {
            if (__state != null && __result != null)
                __result = CompleteRefresh(__result, item, options, __state, cancellationToken);
        }

        private static async Task<ItemUpdateType> CompleteRefresh(Task<ItemUpdateType> task, BaseItem item,
            MetadataRefreshOptions options, RefreshState state, CancellationToken cancellationToken) {
            var result = await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isEnabled) return result;

            // 不运行整个 FFProbeProvider，避免 STRM 探测失败清空主流；复用完整的外挂扫描入口。
            if (Plugin.ExternalFiles?.IsAvailable == true &&
                Plugin.ExternalFiles.HasExternalFilesChanged(item, options.DirectoryService, true)) {
                await Plugin.ExternalFiles.UpdateExternalFiles(item, options, true, cancellationToken)
                    .ConfigureAwait(false);
                result |= ItemUpdateType.MetadataImport;
            }

            var modified = state.FileDateModified;
            if (modified.HasValue && modified.Value.ToUnixTimeSeconds() > 0 &&
                item.DateModified == state.OriginalDateModified && item.HasDateModifiedChanged(modified.Value)) {
                // 只确认刷新开始时看到的版本，不吞掉刷新期间发生的第二次文件变更。
                item.DateModified = modified.Value;
                try {
                    Plugin.LibraryManager.UpdateItems(new List<BaseItem> { item }, null,
                        ItemUpdateType.MetadataImport, false, false, options, cancellationToken);
                }
                catch {
                    item.DateModified = state.OriginalDateModified;
                    throw;
                }
                result |= ItemUpdateType.MetadataImport;
                logger?.Debug($"已忽略 STRM 文件变更并同步修改时间: {item.Path}");
            }
            return result;
        }

        private sealed class RefreshState {
            public DateTimeOffset OriginalDateModified;
            public DateTimeOffset? FileDateModified;
        }
    }
}
