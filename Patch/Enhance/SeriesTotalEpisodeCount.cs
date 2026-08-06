using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     将电视剧与季度 DTO 中展示给前端的未观看集数字段替换为总集数。
    /// </summary>
    public static class SeriesTotalEpisodeCount {
        private static readonly object InitLock = new();

        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;
        private static MethodInfo getBaseItemDtoInternal;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enableSeriesTotalEpisodeCount) {
            lock (InitLock) {
                logger = pluginLogger;
                isEnabled = enableSeriesTotalEpisodeCount;
                if (harmony != null) {
                    Configure(enableSeriesTotalEpisodeCount);
                    return;
                }

                try {
                    var implementationAssembly = Assembly.Load("Emby.Server.Implementations");
                    var implementationVersion = implementationAssembly?.GetName().Version;
                    var dtoServiceType =
                        implementationAssembly?.GetType("Emby.Server.Implementations.Dto.DtoService", false);
                    if (dtoServiceType == null) {
                        PatchLog.InitFailed(logger, nameof(SeriesTotalEpisodeCount), "未找到 DtoService");
                        return;
                    }

                    getBaseItemDtoInternal = PatchMethodResolver.Resolve(
                        dtoServiceType,
                        implementationVersion,
                        new MethodSignatureProfile {
                            Name = "dtoservice-getbaseitemdtointernal",
                            MethodName = "GetBaseItemDtoInternal",
                            BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                            IsStatic = false,
                            ParameterTypes = new[] {
                                typeof(BaseItem),
                                typeof(DtoOptions),
                                typeof(User),
                                typeof(CancellationToken)
                            },
                            ReturnType = typeof(BaseItemDto)
                        },
                        logger,
                        "SeriesTotalEpisodeCount.DtoService.GetBaseItemDtoInternal");

                    if (getBaseItemDtoInternal == null) {
                        PatchLog.InitFailed(logger, nameof(SeriesTotalEpisodeCount), "未找到 GetBaseItemDtoInternal");
                        return;
                    }

                    harmony = new Harmony("mediainfokeeper.seriestotalepisodecount");
                    PatchLog.Patched(logger, nameof(SeriesTotalEpisodeCount), getBaseItemDtoInternal);

                    if (isEnabled) Patch();
                }
                catch (Exception ex) {
                    PatchLog.InitFailed(logger, nameof(SeriesTotalEpisodeCount), ex.Message);
                    logger?.Error("SeriesTotalEpisodeCount 初始化异常：{0}", ex);
                    harmony = null;
                    isPatched = false;
                }
            }
        }

        public static void Configure(bool enableSeriesTotalEpisodeCount) {
            lock (InitLock) {
                isEnabled = enableSeriesTotalEpisodeCount;
                if (harmony == null) return;

                if (isEnabled)
                    Patch();
                else
                    Unpatch();
            }
        }

        private static void Patch() {
            if (isPatched || harmony == null || getBaseItemDtoInternal == null) return;

            harmony.Patch(
                getBaseItemDtoInternal,
                postfix: new HarmonyMethod(typeof(SeriesTotalEpisodeCount), nameof(GetBaseItemDtoInternalPostfix)));
            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null || getBaseItemDtoInternal == null) return;

            harmony.Unpatch(getBaseItemDtoInternal, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetBaseItemDtoInternalPostfix(
            BaseItem item,
            CancellationToken cancellationToken,
            ref BaseItemDto __result) {
            if (!isEnabled || __result?.UserData == null) return;

            try {
                var episodeCount = Plugin.LibraryService?.GetEpisodeCount(item, cancellationToken);

                if (episodeCount.HasValue) {
                    __result.UserData.UnplayedItemCount = episodeCount.Value;
                    __result.UserData.Played = false;
                }
            }
            catch (Exception ex) {
                logger?.Debug("SeriesTotalEpisodeCount failed: {0}", ex.Message);
            }
        }
    }
}
