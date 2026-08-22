using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     调整新建媒体库的提供程序和本地保存默认设置。
    /// </summary>
    public static class LibrayProviderSettings {
        private const string TmdbProviderName = "TheMovieDb";

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo getLibraryOptionsInfo;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable) {
            if (harmony != null) {
                Configure(enable);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;

            try {
                var apiAssembly = Assembly.Load("Emby.Api");
                var apiVersion = apiAssembly?.GetName().Version;
                var libraryServiceType = apiAssembly?.GetType("Emby.Api.Library.LibraryService");
                var requestType = apiAssembly?.GetType("Emby.Api.Library.GetLibraryOptionsInfo");

                if (requestType == null) {
                    PatchLog.InitFailed(logger, nameof(LibrayProviderSettings), "GetLibraryOptionsInfo 类型缺失");
                    return;
                }

                getLibraryOptionsInfo = PatchMethodResolver.Resolve(
                    libraryServiceType,
                    apiVersion,
                    new MethodSignatureProfile {
                        Name = "libraryservice-get-availableoptions-exact",
                        MethodName = "Get",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            requestType
                        },
                        ReturnType = typeof(object)
                    },
                    logger,
                    "LibrayProviderSettings.LibraryService.Get");

                if (getLibraryOptionsInfo == null) {
                    PatchLog.InitFailed(logger, nameof(LibrayProviderSettings), "目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.librayprovidersettings");
                PatchLog.Patched(logger, nameof(LibrayProviderSettings), getLibraryOptionsInfo);

                if (isEnabled) Patch();
            }
            catch (Exception ex) {
                logger?.Error("LibrayProviderSettings 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
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

        private static void Patch() {
            if (isPatched || harmony == null || getLibraryOptionsInfo == null) return;

            harmony.Patch(
                getLibraryOptionsInfo,
                postfix: new HarmonyMethod(typeof(LibrayProviderSettings), nameof(GetLibraryOptionsInfoPostfix)));
            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null || getLibraryOptionsInfo == null) return;

            harmony.Unpatch(getLibraryOptionsInfo, HarmonyPatchType.Postfix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void GetLibraryOptionsInfoPostfix(object __result) {
            if (!isEnabled || !(__result is LibraryOptionsResult result) || result.TypeOptions == null) return;

            if (result.DefaultLibraryOptions != null) {
                result.DefaultLibraryOptions.SaveLocalMetadata = true;
                result.DefaultLibraryOptions.AutoGenerateChapters = false;
                result.DefaultLibraryOptions.PreferredMetadataLanguage = "zh-CN";
                result.DefaultLibraryOptions.MetadataCountryCode = "CN";
                result.DefaultLibraryOptions.PreferredImageLanguage = "zh-CN";
                DisableSubtitleFetchersByDefault(result);
                DisableItemAddedIntroScanByDefault(result);
            }

            foreach (var typeOptions in result.TypeOptions) {
                var metadataNames = NormalizeFetcherOptions(typeOptions?.MetadataFetchers);
                var imageNames = NormalizeFetcherOptions(typeOptions?.ImageFetchers);

                SyncDefaultTypeOptions(result.DefaultLibraryOptions, typeOptions?.Type, metadataNames, imageNames);
            }
        }

        private static void DisableSubtitleFetchersByDefault(LibraryOptionsResult result) {
            if (result?.SubtitleFetchers == null || result.SubtitleFetchers.Length == 0) return;

            foreach (var fetcher in result.SubtitleFetchers) {
                if (fetcher == null) continue;

                fetcher.DefaultEnabled = false;
            }

            var subtitleFetcherNames = result.SubtitleFetchers
                .Where(i => !string.IsNullOrWhiteSpace(i?.Name))
                .Select(i => i.Name)
                .ToArray();

            result.DefaultLibraryOptions.DisabledSubtitleFetchers = subtitleFetcherNames;
            result.DefaultLibraryOptions.SubtitleFetcherOrder = subtitleFetcherNames;
        }

        private static void DisableItemAddedIntroScanByDefault(LibraryOptionsResult result) {
            const string providerName = "MediaInfoKeeper 入库片头扫描";
            var reader = result.MetadataReaders?.FirstOrDefault(i =>
                string.Equals(i?.Name, providerName, StringComparison.OrdinalIgnoreCase));
            if (reader != null) reader.DefaultEnabled = false;

            if (result.DefaultLibraryOptions == null) return;

            var disabledReaders = result.DefaultLibraryOptions.DisabledLocalMetadataReaders ?? Array.Empty<string>();
            if (!disabledReaders.Contains(providerName, StringComparer.OrdinalIgnoreCase))
                result.DefaultLibraryOptions.DisabledLocalMetadataReaders = disabledReaders
                    .Concat(new[] { providerName })
                    .ToArray();
        }

        private static string[] NormalizeFetcherOptions(LibraryOptionInfo[] fetchers) {
            if (fetchers == null || fetchers.Length == 0 ||
                !fetchers.Any(i => string.Equals(i?.Name, TmdbProviderName, StringComparison.OrdinalIgnoreCase)))
                return Array.Empty<string>();

            foreach (var fetcher in fetchers) {
                if (fetcher == null) continue;

                fetcher.DefaultEnabled =
                    string.Equals(fetcher.Name, TmdbProviderName, StringComparison.OrdinalIgnoreCase);
            }

            Array.Sort(fetchers, CompareFetcherOptions);
            return fetchers
                .Where(i => !string.IsNullOrWhiteSpace(i?.Name))
                .Select(i => i.Name)
                .ToArray();
        }

        private static int CompareFetcherOptions(LibraryOptionInfo left, LibraryOptionInfo right) {
            var leftIsTmdb = string.Equals(left?.Name, TmdbProviderName, StringComparison.OrdinalIgnoreCase);
            var rightIsTmdb = string.Equals(right?.Name, TmdbProviderName, StringComparison.OrdinalIgnoreCase);

            if (leftIsTmdb == rightIsTmdb) return 0;

            return leftIsTmdb ? -1 : 1;
        }

        private static void SyncDefaultTypeOptions(
            LibraryOptions libraryOptions,
            string itemType,
            string[] metadataOrder,
            string[] imageOrder) {
            if (libraryOptions == null || string.IsNullOrWhiteSpace(itemType)) return;

            var hasMetadataTmdb = metadataOrder.Any(i =>
                string.Equals(i, TmdbProviderName, StringComparison.OrdinalIgnoreCase));
            var hasImageTmdb = imageOrder.Any(i =>
                string.Equals(i, TmdbProviderName, StringComparison.OrdinalIgnoreCase));

            if (!hasMetadataTmdb && !hasImageTmdb) return;

            var typeOptions = GetOrCreateTypeOptions(libraryOptions, itemType);
            if (typeOptions == null) return;

            if (hasMetadataTmdb) {
                typeOptions.MetadataFetchers = new[] { TmdbProviderName };
                typeOptions.MetadataFetcherOrder = metadataOrder;
            }

            if (hasImageTmdb) {
                typeOptions.ImageFetchers = new[] { TmdbProviderName };
                typeOptions.ImageFetcherOrder = imageOrder;
            }
        }

        private static TypeOptions GetOrCreateTypeOptions(LibraryOptions libraryOptions, string itemType) {
            var existing = (libraryOptions.TypeOptions ?? Array.Empty<TypeOptions>())
                .FirstOrDefault(i => string.Equals(i.Type, itemType, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var typeOptions = new TypeOptions {
                Type = itemType
            };

            libraryOptions.TypeOptions = (libraryOptions.TypeOptions ?? Array.Empty<TypeOptions>())
                .Concat(new[] { typeOptions })
                .ToArray();
            return typeOptions;
        }
    }
}
