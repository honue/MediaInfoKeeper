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
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Data;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using MediaInfoKeeper.External;
using MediaInfoKeeper.Options;
using static MediaInfoKeeper.Options.EnhanceOptions;

namespace MediaInfoKeeper.Patch
{
    public static class MergeMultiVersion
    {
        private static readonly AsyncLocal<BaseItem[]> currentAllCollectionFolders = new AsyncLocal<BaseItem[]>();

        private static Harmony harmony;
        private static ILogger logger;
        private static MethodInfo isEligibleForMultiVersion;
        private static MethodInfo canRefreshImage;
        private static MethodInfo addLibrariesToPresentationUniqueKey;
        private static MethodInfo getRefreshOptions;
        private static bool isEnabled;
        private static bool isPatched;
        private static MergeSeriesScopeOption mergeSeriesPreference;

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enable, MergeSeriesScopeOption seriesPreference)
        {
            if (harmony != null)
            {
                Configure(enable, seriesPreference);
                return;
            }

            logger = pluginLogger;
            isEnabled = enable;
            mergeSeriesPreference = seriesPreference;
            isPatched = false;

            try
            {
                var namingAssembly = Assembly.Load("Emby.Naming");
                var namingVersion = namingAssembly?.GetName().Version;
                var videoListResolverType = namingAssembly?.GetType("Emby.Naming.Video.VideoListResolver");
                if (videoListResolverType == null)
                {
                    FailInitialization("Emby.Naming.Video.VideoListResolver 类型缺失");
                    return;
                }

                isEligibleForMultiVersion = PatchMethodResolver.Resolve(
                    videoListResolverType,
                    namingVersion,
                    new MethodSignatureProfile
                    {
                        Name = "video-resolver-iseligible-exact",
                        MethodName = "IsEligibleForMultiVersion",
                        BindingFlags = BindingFlags.Static | BindingFlags.NonPublic,
                        ParameterTypes = new[] { typeof(string), typeof(string) },
                        ReturnType = typeof(bool)
                    },
                    logger,
                    "MergeMultiVersion.VideoListResolver.IsEligibleForMultiVersion");

                var providersAssembly = Assembly.Load("Emby.Providers");
                var providerManagerType = providersAssembly?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManagerType == null)
                {
                    FailInitialization("Emby.Providers.Manager.ProviderManager 类型缺失");
                    return;
                }

                canRefreshImage = PatchMethodResolver.Resolve(
                    providerManagerType,
                    providersAssembly?.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "provider-canrefresh-image-exact",
                        MethodName = "CanRefresh",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(IImageProvider),
                            typeof(BaseItem),
                            typeof(LibraryOptions),
                            typeof(ImageRefreshOptions),
                            typeof(bool),
                            typeof(bool)
                        },
                        ReturnType = typeof(bool)
                    },
                    logger,
                    "MergeMultiVersion.ProviderManager.CanRefresh");

                addLibrariesToPresentationUniqueKey = PatchMethodResolver.Resolve(
                    typeof(Series),
                    typeof(Series).Assembly.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "series-addlibraries-exact",
                        MethodName = "AddLibrariesToPresentationUniqueKey",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        ParameterTypes = new[]
                        {
                            typeof(string),
                            typeof(BaseItem[]),
                            typeof(LibraryOptions),
                            typeof(IDataContext)
                        },
                        ReturnType = typeof(string)
                    },
                    logger,
                    "MergeMultiVersion.Series.AddLibrariesToPresentationUniqueKey");

                var apiAssembly = Assembly.Load("Emby.Api");
                var itemRefreshServiceType = apiAssembly?.GetType("Emby.Api.ItemRefreshService");
                var refreshItemType = apiAssembly?.GetType("Emby.Api.RefreshItem");
                if (itemRefreshServiceType == null)
                {
                    FailInitialization("Emby.Api.ItemRefreshService 类型缺失");
                    return;
                }

                if (refreshItemType == null)
                {
                    FailInitialization("Emby.Api.RefreshItem 类型缺失");
                    return;
                }

                getRefreshOptions = PatchMethodResolver.Resolve(
                    itemRefreshServiceType,
                    apiAssembly?.GetName().Version,
                    new MethodSignatureProfile
                    {
                        Name = "itemrefresh-getoptions-exact",
                        MethodName = "GetRefreshOptions",
                        BindingFlags = BindingFlags.Instance | BindingFlags.NonPublic,
                        ParameterTypes = new[] { refreshItemType },
                        ReturnType = typeof(MetadataRefreshOptions)
                    },
                    logger,
                    "MergeMultiVersion.ItemRefreshService.GetRefreshOptions");

                if (isEligibleForMultiVersion == null)
                {
                    FailInitialization("IsEligibleForMultiVersion 缺失");
                    return;
                }

                if (canRefreshImage == null)
                {
                    FailInitialization("CanRefresh(IImageProvider, BaseItem, LibraryOptions, ImageRefreshOptions, bool, bool) 缺失");
                    return;
                }

                if (addLibrariesToPresentationUniqueKey == null)
                {
                    FailInitialization("Series.AddLibrariesToPresentationUniqueKey 缺失");
                    return;
                }

                if (getRefreshOptions == null)
                {
                    FailInitialization("GetRefreshOptions 缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.mergemultiversion");

                if (isEnabled)
                {
                    Patch();
                }
            }
            catch (Exception ex)
            {
                logger?.Error("MergeMultiVersion 初始化失败。");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                isPatched = false;
                isEnabled = false;
            }
        }

        private static void FailInitialization(string reason)
        {
            PatchLog.InitFailed(logger, nameof(MergeMultiVersion), reason);
            harmony = null;
            isPatched = false;
            isEnabled = false;
        }

        public static void Configure(bool enable, MergeSeriesScopeOption seriesPreference)
        {
            isEnabled = enable;
            mergeSeriesPreference = seriesPreference;

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
        }

        private static void Patch()
        {
            if (isPatched || harmony == null)
            {
                return;
            }

            if (isEligibleForMultiVersion != null)
            {
                harmony.Patch(
                    isEligibleForMultiVersion,
                    prefix: new HarmonyMethod(typeof(MergeMultiVersion), nameof(IsEligibleForMultiVersionPrefix)));
                PatchLog.Patched(logger, nameof(MergeMultiVersion), isEligibleForMultiVersion);
            }

            if (canRefreshImage != null)
            {
                harmony.Patch(
                    canRefreshImage,
                    prefix: new HarmonyMethod(typeof(MergeMultiVersion), nameof(CanRefreshImagePrefix)));
                PatchLog.Patched(logger, nameof(MergeMultiVersion), canRefreshImage);
            }

            if (addLibrariesToPresentationUniqueKey != null)
            {
                harmony.Patch(
                    addLibrariesToPresentationUniqueKey,
                    prefix: new HarmonyMethod(typeof(MergeMultiVersion),
                        nameof(AddLibrariesToPresentationUniqueKeyPrefix)));
                PatchLog.Patched(logger, nameof(MergeMultiVersion), addLibrariesToPresentationUniqueKey);
            }

            if (getRefreshOptions != null)
            {
                harmony.Patch(
                    getRefreshOptions,
                    postfix: new HarmonyMethod(typeof(MergeMultiVersion),
                        nameof(GetRefreshOptionsPostfix)));
                PatchLog.Patched(logger, nameof(MergeMultiVersion), getRefreshOptions);
            }

            isPatched = true;
        }

        private static void Unpatch()
        {
            if (!isPatched || harmony == null)
            {
                return;
            }

            if (isEligibleForMultiVersion != null)
            {
                harmony.Unpatch(isEligibleForMultiVersion, HarmonyPatchType.Prefix, harmony.Id);
            }

            if (canRefreshImage != null)
            {
                harmony.Unpatch(canRefreshImage, HarmonyPatchType.Prefix, harmony.Id);
            }

            if (addLibrariesToPresentationUniqueKey != null)
            {
                harmony.Unpatch(addLibrariesToPresentationUniqueKey, HarmonyPatchType.Prefix, harmony.Id);
            }

            if (getRefreshOptions != null)
            {
                harmony.Unpatch(getRefreshOptions, HarmonyPatchType.Postfix, harmony.Id);
            }

            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool IsEligibleForMultiVersionPrefix(string folderName, string testFilename, ref bool __result)
        {
            __result = string.Equals(folderName, Path.GetFileName(Path.GetDirectoryName(testFilename)),
                StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static BaseItem[] GetAllCollectionFolders(Series series)
        {
            if (!(series.HasProviderId(MetadataProviders.Tmdb) || series.HasProviderId(MetadataProviders.Imdb) ||
                  series.HasProviderId(MetadataProviders.Tvdb)))
            {
                return Array.Empty<BaseItem>();
            }

            var allSeries = BaseItem.LibraryManager.GetItemList(new InternalItemsQuery
            {
                EnableTotalRecordCount = false,
                Recursive = false,
                ExcludeItemIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { nameof(Series) },
                AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>(MetadataProviders.Tmdb.ToString(),
                        series.GetProviderId(MetadataProviders.Tmdb)),
                    new KeyValuePair<string, string>(MetadataProviders.Imdb.ToString(),
                        series.GetProviderId(MetadataProviders.Imdb)),
                    new KeyValuePair<string, string>(MetadataProviders.Tvdb.ToString(),
                        series.GetProviderId(MetadataProviders.Tvdb))
                }
            }).Concat(new[] { series }).ToList();

            var collectionFolders = new HashSet<BaseItem>();

            foreach (var item in allSeries)
            {
                var options = BaseItem.LibraryManager.GetLibraryOptions(item);

                if (options.EnableAutomaticSeriesGrouping)
                {
                    foreach (var library in BaseItem.LibraryManager.GetCollectionFolders(item))
                    {
                        collectionFolders.Add(library);
                    }
                }
            }

            return collectionFolders.OrderBy(c => c.InternalId).ToArray();
        }

        [HarmonyPrefix]
        private static void CanRefreshImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions,
            ImageRefreshOptions refreshOptions, bool ignoreMetadataLock, bool ignoreLibraryOptions)
        {
            if (currentAllCollectionFolders.Value != null)
            {
                return;
            }

            if (item.Parent is null && item.ExtraType is null)
            {
                return;
            }

            if (item is Series series && mergeSeriesPreference == MergeSeriesScopeOption.GlobalScope)
            {
                currentAllCollectionFolders.Value = GetAllCollectionFolders(series);
            }
        }

        [HarmonyPrefix]
        private static bool AddLibrariesToPresentationUniqueKeyPrefix(Series __instance, string key,
            ref BaseItem[] collectionFolders, LibraryOptions libraryOptions, IDataContext dataContext,
            ref string __result)
        {
            if (currentAllCollectionFolders.Value != null)
            {
                if (currentAllCollectionFolders.Value.Length > 1)
                {
                    collectionFolders = currentAllCollectionFolders.Value;
                }

                currentAllCollectionFolders.Value = null;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void GetRefreshOptionsPostfix(IReturnVoid request, MetadataRefreshOptions __result)
        {
            var id = Traverse.Create(request).Property("Id").GetValue<string>();
            var item = BaseItem.LibraryManager.GetItemById(id);

            if (item is Series || item is Season)
            {
                var series = item as Series ?? (item as Season).Series;
                var seriesTmdbId = series?.GetProviderId(MetadataProviders.Tmdb);
                var episodeGroupId = series?.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();

                var itemsToRefresh = BaseItem.LibraryManager.GetItemList(new InternalItemsQuery
                {
                    PresentationUniqueKey = item.PresentationUniqueKey,
                    ExcludeItemIds = new[] { item.InternalId }
                });

                foreach (var alt in itemsToRefresh)
                {
                    if (!string.IsNullOrEmpty(episodeGroupId))
                    {
                        var altSeries = alt as Series ?? (alt as Season)?.Series;

                        if (altSeries != null)
                        {
                            var altSeriesTmdbId = altSeries.GetProviderId(MetadataProviders.Tmdb);
                            var altEpisodeGroupId = altSeries.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName);
                            if (string.IsNullOrEmpty(altEpisodeGroupId) && !string.IsNullOrEmpty(seriesTmdbId) &&
                                !string.IsNullOrEmpty(altSeriesTmdbId) && string.Equals(seriesTmdbId, altSeriesTmdbId,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                alt.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, episodeGroupId);
                                alt.UpdateToRepository(ItemUpdateType.MetadataEdit);
                            }
                        }
                    }

                    BaseItem.ProviderManager.QueueRefresh(alt.InternalId, __result, RefreshPriority.Normal, true);
                }
            }
        }
    }
}
