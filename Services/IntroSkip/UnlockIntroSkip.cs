using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public static class UnlockIntroSkip
    {
        private static readonly AsyncLocal<BaseItem> ShortcutItem = new AsyncLocal<BaseItem>();
        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo isIntroDetectionSupported;
        private static MethodInfo createQueryForEpisodeIntroDetection;
        private static MethodInfo isShortcutGetter;
        private static bool isEnabled;
        private static bool isPatched;
        private static List<string> libraryPathsInScope = new List<string>();

        public static void Initialize(ILogger pluginLogger, bool enable)
        {
            if (harmony != null)
            {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders?.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                isIntroDetectionSupported = audioFingerprintManager?.GetMethod("IsIntroDetectionSupported",
                    BindingFlags.Public | BindingFlags.Instance);

                var markerScheduledTask = embyProviders?.GetType("Emby.Providers.Markers.MarkerScheduledTask");
                createQueryForEpisodeIntroDetection = markerScheduledTask?.GetMethod(
                    "CreateQueryForEpisodeIntroDetection",
                    BindingFlags.Public | BindingFlags.Static);

                isShortcutGetter = typeof(BaseItem).GetProperty("IsShortcut", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetGetMethod();

                if (isIntroDetectionSupported == null || createQueryForEpisodeIntroDetection == null || isShortcutGetter == null)
                {
                    logger?.Warn("UnlockIntroSkip 初始化失败：目标方法缺失。");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.introskip");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception e)
            {
                logger?.Error("UnlockIntroSkip 初始化失败。");
                logger?.Error(e.Message);
                logger?.Error(e.ToString());
                harmony = null;
                isEnabled = false;
            }
        }

        public static void Configure(bool enable)
        {
            isEnabled = enable;

            if (harmony == null)
            {
                return;
            }

            if (isEnabled)
            {
                Patch();
            }
            else
            {
                Unpatch();
            }

            logger?.Info("UnlockIntroSkip " + (isEnabled ? "已启用" : "已禁用"));
        }

        public static void Configure(MediaInfoKeeper.Configuration.PluginConfiguration options)
        {
            if (options?.IntroSkip == null)
            {
                return;
            }

            Configure(options.IntroSkip.UnlockIntroSkip);
            UpdateLibraryPathsInScope(options.IntroSkip.MarkerEnabledLibraryScope);
            UpdateLibraryIntroDetectionFingerprintLength(options.IntroSkip.IntroDetectionFingerprintMinutes);
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            harmony.Patch(isShortcutGetter,
                prefix: new HarmonyMethod(typeof(UnlockIntroSkip), nameof(IsShortcutPrefix)));
            harmony.Patch(isIntroDetectionSupported,
                prefix: new HarmonyMethod(typeof(UnlockIntroSkip), nameof(IsIntroDetectionSupportedPrefix)),
                postfix: new HarmonyMethod(typeof(UnlockIntroSkip), nameof(IsIntroDetectionSupportedPostfix)));
            harmony.Patch(createQueryForEpisodeIntroDetection,
                postfix: new HarmonyMethod(typeof(UnlockIntroSkip), nameof(CreateQueryForEpisodeIntroDetectionPostfix)));

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            harmony.Unpatch(isShortcutGetter, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(isIntroDetectionSupported, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(isIntroDetectionSupported, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(createQueryForEpisodeIntroDetection, HarmonyPatchType.Postfix, harmony.Id);

            isPatched = false;
        }

        private static void UpdateLibraryPathsInScope(string currentScope)
        {
            if (Plugin.LibraryManager == null)
            {
                return;
            }

            var libraryIds = currentScope?.Split(new[] { ',', ';', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim()).ToArray();

            var folders = Plugin.LibraryManager.GetVirtualFolders()
                .Where(f => libraryIds != null && libraryIds.Any()
                    ? libraryIds.Contains(f.ItemId)
                    : f.LibraryOptions.EnableMarkerDetection &&
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

        private static void UpdateLibraryIntroDetectionFingerprintLength(int currentLength)
        {
            if (Plugin.LibraryManager == null)
            {
                return;
            }

            var libraries = Plugin.LibraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;

                if (options.IntroDetectionFingerprintLength != currentLength &&
                    long.TryParse(library.ItemId, out var itemId))
                {
                    options.IntroDetectionFingerprintLength = currentLength;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        [HarmonyPrefix]
        private static bool IsShortcutPrefix(BaseItem __instance, ref bool __result)
        {
            if (ShortcutItem.Value != null && __instance.InternalId == ShortcutItem.Value.InternalId)
            {
                __result = false;
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool IsIntroDetectionSupportedPrefix(Episode item, LibraryOptions libraryOptions,
            out bool __state)
        {
            __state = false;

            if (item != null && item.IsShortcut)
            {
                ShortcutItem.Value = item;
                __state = true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void IsIntroDetectionSupportedPostfix(Episode item, LibraryOptions libraryOptions,
            bool __state)
        {
            if (__state)
            {
                ShortcutItem.Value = null;
            }
        }

        [HarmonyPostfix]
        private static void CreateQueryForEpisodeIntroDetectionPostfix(LibraryOptions libraryOptions,
            ref InternalItemsQuery __result)
        {
            if (libraryPathsInScope != null && libraryPathsInScope.Any())
            {
                __result.PathStartsWithAny = libraryPathsInScope.ToArray();
            }
        }
    }
}
