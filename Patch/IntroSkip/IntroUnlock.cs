using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Options;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     放宽快捷方式和指定电视剧库的片头检测限制，并同步相关参数。
    /// </summary>
    public static class IntroUnlock {
        private static readonly AsyncLocal<BaseItem> ShortcutItem = new();
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo isIntroDetectionSupported;
        private static MethodInfo createQueryForEpisodeIntroDetection;
        private static MethodInfo isShortcutGetter;
        private static bool isEnabled;
        private static bool isPatched;
        private static List<string> libraryPathsInScope = new();
        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable) {
            if (harmony != null) {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders?.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                var providersVersion = embyProviders?.GetName().Version;
                isIntroDetectionSupported = PatchMethodResolver.Resolve(
                    audioFingerprintManager,
                    providersVersion,
                    new MethodSignatureProfile {
                        Name = "audiofingerprintmanager-isintrodetectionsupported-exact",
                        MethodName = "IsIntroDetectionSupported",
                        BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                        ParameterTypes = new[] { typeof(Episode), typeof(LibraryOptions) }
                    },
                    logger,
                    "UnlockIntroSkip.IsIntroDetectionSupported");

                var markerScheduledTask = embyProviders?.GetType("Emby.Providers.Markers.MarkerScheduledTask");
                createQueryForEpisodeIntroDetection = PatchMethodResolver.Resolve(
                    markerScheduledTask,
                    providersVersion,
                    new MethodSignatureProfile {
                        Name = "markerscheduledtask-createqueryforepisodeintrodetection-exact",
                        MethodName = "CreateQueryForEpisodeIntroDetection",
                        BindingFlags = BindingFlags.Public | BindingFlags.Static,
                        ParameterTypes = new[] { typeof(LibraryOptions) }
                    },
                    logger,
                    "UnlockIntroSkip.CreateQueryForEpisodeIntroDetection");

                var controllerVersion = typeof(BaseItem).Assembly.GetName().Version;
                isShortcutGetter = PatchMethodResolver.Resolve(
                    typeof(BaseItem),
                    controllerVersion,
                    new MethodSignatureProfile {
                        Name = "baseitem-get_isshortcut-exact",
                        MethodName = "get_IsShortcut",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = Type.EmptyTypes,
                        ReturnType = typeof(bool)
                    },
                    logger,
                    "UnlockIntroSkip.get_IsShortcut");

                if (isIntroDetectionSupported == null || createQueryForEpisodeIntroDetection == null ||
                    isShortcutGetter == null) {
                    PatchLog.InitFailed(logger, nameof(IntroUnlock), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.introskip");

                if (isEnabled) Patch();
            }
            catch (Exception e) {
                logger?.Error("UnlockIntroSkip 初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable) {
            isEnabled = enable;

            if (harmony == null) return;

            if (isEnabled)
                Patch();
            else
                Unpatch();
        }

        public static void Configure(PluginConfiguration options) {
            if (options?.IntroSkip == null) return;

            UpdateLibraryPathsInScope();
            UpdateLibraryIntroDetectionFingerprintLength(options.IntroSkip.IntroDetectionFingerprintMinutes);
        }

        private static void Patch() {
            if (isPatched || harmony == null) return;

            harmony.Patch(isShortcutGetter,
                new HarmonyMethod(typeof(IntroUnlock), nameof(IsShortcutPrefix)));
            harmony.Patch(isIntroDetectionSupported,
                new HarmonyMethod(typeof(IntroUnlock), nameof(IsIntroDetectionSupportedPrefix)),
                new HarmonyMethod(typeof(IntroUnlock), nameof(IsIntroDetectionSupportedPostfix)));
            harmony.Patch(createQueryForEpisodeIntroDetection,
                postfix: new HarmonyMethod(typeof(IntroUnlock), nameof(CreateQueryForEpisodeIntroDetectionPostfix)));

            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null) return;

            harmony.Unpatch(isShortcutGetter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(isIntroDetectionSupported, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(isIntroDetectionSupported, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(createQueryForEpisodeIntroDetection, HarmonyPatchType.Postfix, harmony.Id);

            isPatched = false;
        }

        private static void UpdateLibraryPathsInScope() {
            if (Plugin.LibraryManager == null) return;

            var folders = Plugin.LibraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableMarkerDetection &&
                            (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null))
                .ToList();

            libraryPathsInScope = folders
                .SelectMany(f => f.Locations)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? p
                    : p + Path.DirectorySeparatorChar)
                .ToList();
        }

        private static void UpdateLibraryIntroDetectionFingerprintLength(int currentLength) {
            if (Plugin.LibraryManager == null) return;

            var libraries = Plugin.LibraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries) {
                var options = library.LibraryOptions;

                if (options.IntroDetectionFingerprintLength != currentLength &&
                    long.TryParse(library.ItemId, out var itemId)) {
                    options.IntroDetectionFingerprintLength = currentLength;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        [HarmonyPrefix]
        private static bool IsShortcutPrefix(BaseItem __instance, ref bool __result) {
            if (ShortcutItem.Value != null && __instance.InternalId == ShortcutItem.Value.InternalId) {
                __result = false;
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool IsIntroDetectionSupportedPrefix(Episode item, LibraryOptions libraryOptions,
            out bool __state) {
            __state = false;

            if (item != null && item.IsShortcut) {
                ShortcutItem.Value = item;
                __state = true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void IsIntroDetectionSupportedPostfix(Episode item, LibraryOptions libraryOptions,
            bool __state) {
            if (__state) ShortcutItem.Value = null;
        }

        [HarmonyPostfix]
        private static void CreateQueryForEpisodeIntroDetectionPostfix(LibraryOptions libraryOptions,
            ref InternalItemsQuery __result) {
            if (libraryPathsInScope != null && libraryPathsInScope.Any()) __result.PathStartsWithAny = libraryPathsInScope.ToArray();
        }
    }
}
