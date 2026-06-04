using System;
using System.Collections.Generic;
using System.Linq;
using MediaInfoKeeper.Options;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch
{
    /// <summary>
    /// 集中初始化各补丁，并统一计算其启用状态、等待状态与说明信息。
    /// </summary>
    public static class PatchManager
    {
        private sealed class PatchRegistration
        {
            public string Name { get; set; }

            public PatchApproach Approach { get; set; } = PatchApproach.Harmony;

            public Action<PluginConfiguration> Initialize { get; set; }

            public Action<PluginConfiguration> Configure { get; set; }

            public Func<PluginConfiguration, bool> IsEnabled { get; set; }

            public Func<bool> IsReady { get; set; }

            public Func<bool> IsWaiting { get; set; }

            public Func<string> Notes { get; set; }
        }

        private static readonly object InitLock = new object();
        private static readonly List<PatchTracker> trackers = new List<PatchTracker>();
        private static readonly List<PatchRegistration> registrations = new List<PatchRegistration>();

        private static bool initialized;
        private static ILogger logger;
        private static IActivityManager activityManager;
        private static string lastReportedFailureKey;

        public static IReadOnlyCollection<PatchTracker> Trackers => trackers.AsReadOnly();

        public static void Initialize(ILogger pluginLogger, PluginConfiguration options, IActivityManager pluginActivityManager = null)
        {
            lock (InitLock)
            {
                logger = pluginLogger;
                activityManager = pluginActivityManager;
                var safeOptions = EnsureOptions(options);

                if (!initialized)
                {
                    initialized = true;
                    trackers.Clear();
                    registrations.Clear();
                    BuildRegistrations();
                    RegisterTrackers();
                }

                foreach (var registration in registrations)
                {
                    registration.Initialize?.Invoke(safeOptions);
                }

                Configure(safeOptions);
            }
        }

        public static void Configure(PluginConfiguration options)
        {
            lock (InitLock)
            {
                var safeOptions = EnsureOptions(options);

                foreach (var registration in registrations)
                {
                    registration.Configure?.Invoke(safeOptions);

                    var tracker = trackers.FirstOrDefault(t => string.Equals(t.Name, registration.Name, StringComparison.Ordinal));
                    if (tracker == null)
                    {
                        continue;
                    }

                    var enabled = registration.IsEnabled?.Invoke(safeOptions) == true;
                    var waiting = registration.IsWaiting?.Invoke() == true;
                    var ready = registration.IsReady?.Invoke() == true;

                    tracker.IsEnabled = enabled;
                    tracker.Notes = registration.Notes?.Invoke();

                    if (!enabled)
                    {
                        tracker.Health = PatchHealth.Disabled;
                    }
                    else if (waiting)
                    {
                        tracker.Health = PatchHealth.Waiting;
                    }
                    else
                    {
                        tracker.Health = ready ? PatchHealth.Enabled : PatchHealth.Failed;
                    }
                }

                LogTrackerSummary();
                ReportFailedEnabledPatches();
            }
        }

        public static bool? IsHarmonyHealthy()
        {
            if (!trackers.Any())
            {
                return null;
            }

            var enabledTrackers = trackers.Where(t => t.IsEnabled).ToArray();
            if (enabledTrackers.Length == 0)
            {
                return null;
            }

            return enabledTrackers.All(t => t.Approach == PatchApproach.Harmony);
        }

        private static void BuildRegistrations()
        {
            registrations.Add(new PatchRegistration
            {
                Name = "FfprobeGuard",
                Initialize = options => FfProcessGuard.Initialize(logger, GetFfProcessGuardEnabled(options)),
                Configure = options => FfProcessGuard.Configure(IsPluginEnabled(options) && GetFfProcessGuardEnabled(options)),
                IsEnabled = options => IsPluginEnabled(options) && GetFfProcessGuardEnabled(options),
                IsReady = () => FfProcessGuard.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "ProviderManager",
                Initialize = _ => ProviderManager.Initialize(logger, true),
                Configure = _ => ProviderManager.Configure(true),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => ProviderManager.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "IsoMountedProbeInput",
                Initialize = _ => IsoMountedProbeInput.Initialize(logger, true),
                Configure = _ => IsoMountedProbeInput.Configure(true),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => IsoMountedProbeInput.IsReady,
                Notes = () => "mount iso before probe"
            });

            registrations.Add(new PatchRegistration
            {
                Name = "IsoProbeSupport",
                Initialize = _ => IsoProbeSupport.Initialize(logger, true),
                Configure = _ => IsoProbeSupport.Configure(true),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => IsoProbeSupport.IsReady,
                Notes = () =>
                {
                    var mediaInfoOptions = Plugin.Instance?.Options?.GetMediaInfoOptions();
                    if (string.IsNullOrWhiteSpace(mediaInfoOptions?.CustomFfprobePath))
                    {
                        return "inactive";
                    }

                    return null;
                }
            });

            registrations.Add(new PatchRegistration
            {
                Name = "IsoFfprobePath",
                Initialize = _ => IsoFfprobePath.Initialize(logger, true),
                Configure = _ => IsoFfprobePath.Configure(true),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => IsoFfprobePath.IsReady,
                Notes = () =>
                {
                    var mediaInfoOptions = Plugin.Instance?.Options?.GetMediaInfoOptions();
                    if (string.IsNullOrWhiteSpace(mediaInfoOptions?.CustomFfprobePath))
                    {
                        return "inactive";
                    }

                    return null;
                }
            });

            registrations.Add(new PatchRegistration
            {
                Name = "StrmMediaInfoGuard",
                Initialize = _ => MediaInfoClearGuard.Initialize(logger, true),
                Configure = _ => MediaInfoClearGuard.Configure(true),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => MediaInfoClearGuard.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "DetailTriggerMediaInfo",
                Initialize = options => DetailTriggerMediaInfo.Initialize(
                    logger,
                    IsPluginEnabled(options) && options.GetMediaInfoOptions().ExtractMediaInfoOnItemDetail),
                Configure = options => DetailTriggerMediaInfo.Configure(
                    IsPluginEnabled(options) && options.GetMediaInfoOptions().ExtractMediaInfoOnItemDetail),
                IsEnabled = options => IsPluginEnabled(options) && options.GetMediaInfoOptions().ExtractMediaInfoOnItemDetail,
                IsReady = () => DetailTriggerMediaInfo.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "LibraryMonitorDelay",
                Initialize = options => LibraryMonitorDelay.Initialize(
                    logger,
                    IsPluginEnabled(options) && options.MainPage.FileChangeRefreshDelaySeconds >= 0,
                    options.MainPage.FileChangeRefreshDelaySeconds),
                Configure = options => LibraryMonitorDelay.Configure(
                    IsPluginEnabled(options) && options.MainPage.FileChangeRefreshDelaySeconds >= 0,
                    options.MainPage.FileChangeRefreshDelaySeconds),
                IsEnabled = options => IsPluginEnabled(options) && options.MainPage.FileChangeRefreshDelaySeconds >= 0,
                IsReady = () => LibraryMonitorDelay.IsReady,
                Notes = () =>
                {
                    var delaySeconds = Plugin.Instance?.Options?.MainPage?.FileChangeRefreshDelaySeconds ?? -1;
                    return delaySeconds >= 0 ? $"override LibraryMonitorDelaySeconds={delaySeconds}" : null;
                }
            });

            registrations.Add(new PatchRegistration
            {
                Name = "PlaybackFfprocessAllowance",
                Initialize = _ => PlaybackFfprocess.Initialize(logger, true),
                Configure = _ => PlaybackFfprocess.Configure(true),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => PlaybackFfprocess.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "ImageCapture",
                Initialize = options => ImageCapture.Initialize(logger, options.MetaData.EnableImageCapture),
                Configure = options => ImageCapture.Configure(IsPluginEnabled(options) && options.MetaData.EnableImageCapture),
                IsEnabled = options => IsPluginEnabled(options) && options.MetaData.EnableImageCapture,
                IsReady = () => ImageCapture.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "ItemImageClearGuard",
                Initialize = _ => ImageClearGuard.Initialize(logger, true),
                Configure = options => ImageClearGuard.Configure(IsPluginEnabled(options)),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => ImageClearGuard.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "MovieDbTitle",
                Initialize = options => MovieDbTitle.Initialize(logger, options.MetaData.EnableAlternativeTitleFallback),
                Configure = options => MovieDbTitle.Configure(IsPluginEnabled(options) && options.MetaData.EnableAlternativeTitleFallback),
                IsEnabled = options => IsPluginEnabled(options) && options.MetaData.EnableAlternativeTitleFallback,
                IsReady = () => MovieDbTitle.IsReady,
                IsWaiting = () => MovieDbTitle.IsWaiting,
                Notes = () => MovieDbTitle.IsWaiting ? "waiting for MovieDb assembly" : null
            });

            registrations.Add(new PatchRegistration
            {
                Name = "TmdbPersonUpdate",
                Initialize = _ => TmdbPersonUpdate.Initialize(logger, true),
                Configure = _ => TmdbPersonUpdate.Configure(true),
                IsEnabled = _ => true,
                IsReady = () => TmdbPersonUpdate.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "TvdbTitle",
                Initialize = options => TvdbTitle.Initialize(logger, options.MetaData.EnableTvdbFallback),
                Configure = options => TvdbTitle.Configure(IsPluginEnabled(options) && options.MetaData.EnableTvdbFallback),
                IsEnabled = options => IsPluginEnabled(options) && options.MetaData.EnableTvdbFallback,
                IsReady = () => TvdbTitle.IsReady,
                IsWaiting = () => TvdbTitle.IsWaiting,
                Notes = () => TvdbTitle.IsWaiting ? "waiting for Tvdb assembly" : null
            });

            registrations.Add(new PatchRegistration
            {
                Name = "MovieDbEpisodeGroup",
                Initialize = options => MovieDbEpisodeGroup.Initialize(
                    logger,
                    options.MetaData.EnableMovieDbEpisodeGroup,
                    options.MetaData.EnableLocalEpisodeGroup),
                Configure = options => MovieDbEpisodeGroup.Configure(
                    IsPluginEnabled(options) && options.MetaData.EnableMovieDbEpisodeGroup,
                    IsPluginEnabled(options) && options.MetaData.EnableLocalEpisodeGroup),
                IsEnabled = options => IsPluginEnabled(options) && options.MetaData.EnableMovieDbEpisodeGroup,
                IsReady = () => MovieDbEpisodeGroup.IsReady,
                IsWaiting = () => MovieDbEpisodeGroup.IsWaiting,
                Notes = () => MovieDbEpisodeGroup.IsWaiting ? "waiting for MovieDb assembly" : null
            });

            registrations.Add(new PatchRegistration
            {
                Name = "EnhanceMissingEpisodes",
                Initialize = options => EnhanceMissingEpisodes.Initialize(
                    logger,
                    options.MetaData.EnableMissingEpisodesEnhance),
                Configure = options => EnhanceMissingEpisodes.Configure(
                    IsPluginEnabled(options) && options.MetaData.EnableMissingEpisodesEnhance),
                IsEnabled = options => IsPluginEnabled(options) && options.MetaData.EnableMissingEpisodesEnhance,
                IsReady = () => EnhanceMissingEpisodes.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "OriginalPoster",
                Initialize = options => OriginalPoster.Initialize(logger, options.MetaData.EnableOriginalPoster),
                Configure = options => OriginalPoster.Configure(IsPluginEnabled(options) && options.MetaData.EnableOriginalPoster),
                IsEnabled = options => IsPluginEnabled(options) && options.MetaData.EnableOriginalPoster,
                IsReady = () => OriginalPoster.IsReady,
                IsWaiting = () => OriginalPoster.IsWaiting,
                Notes = () => null
            });

            registrations.Add(new PatchRegistration
            {
                Name = "UnlockIntroSkip",
                Initialize = options =>
                {
                    IntroUnlock.Initialize(logger, options.IntroSkip.UnlockIntroSkip);
                    IntroUnlock.Configure(options);
                },
                Configure = options =>
                {
                    IntroUnlock.Configure(IsPluginEnabled(options) && options.IntroSkip.UnlockIntroSkip);
                    IntroUnlock.Configure(options);
                    if (!IsPluginEnabled(options))
                    {
                        IntroUnlock.Configure(false);
                    }
                },
                IsEnabled = options => IsPluginEnabled(options) && options.IntroSkip.UnlockIntroSkip,
                IsReady = () => IntroUnlock.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "IntroMarkerProtect",
                Initialize = _ => IntroMarkerProtect.Initialize(logger, true),
                Configure = options => IntroMarkerProtect.Configure(IsPluginEnabled(options)),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => IntroMarkerProtect.IsReady,
                Notes = () => IsPluginEnabled(Plugin.Instance?.Options) ? "always enabled" : null
            });

            registrations.Add(new PatchRegistration
            {
                Name = "EmbeddedChapterMarkerMap",
                Initialize = options => EmbeddedChapterMarkerMap.Initialize(logger, IsPluginEnabled(options)),
                Configure = options => EmbeddedChapterMarkerMap.Configure(IsPluginEnabled(options)),
                IsEnabled = options => IsPluginEnabled(options),
                IsReady = () => EmbeddedChapterMarkerMap.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "NetworkServer",
                Initialize = options => NetworkServer.Initialize(logger, IsPluginEnabled(options) && HasNetworkServerFeatures(options)),
                Configure = options => NetworkServer.Configure(IsPluginEnabled(options) && HasNetworkServerFeatures(options)),
                IsEnabled = options => IsPluginEnabled(options) && HasNetworkServerFeatures(options),
                IsReady = () => NetworkServer.IsReady,
                Notes = BuildProxyNotes
            });

            registrations.Add(new PatchRegistration
            {
                Name = "LocalDiscoveryAddress",
                Initialize = options => LocalDiscoveryAddress.Initialize(logger, options.GetNetWorkOptions().CustomLocalDiscoveryAddress),
                Configure = options => LocalDiscoveryAddress.Configure(
                    IsPluginEnabled(options) ? options.GetNetWorkOptions().CustomLocalDiscoveryAddress : string.Empty),
                IsEnabled = options =>
                    IsPluginEnabled(options) &&
                    !string.IsNullOrWhiteSpace(options.GetNetWorkOptions().CustomLocalDiscoveryAddress),
                IsReady = () => LocalDiscoveryAddress.IsConfiguredBehaviorReady,
                Notes = BuildLocalDiscoveryNotes
            });

            registrations.Add(new PatchRegistration
            {
                Name = "ChineseSearch",
                Initialize = options =>
                {
                    ChineseSearch.Initialize(logger, options.Enhance);
                },
                Configure = options =>
                {
                    ChineseSearch.Configure(
                        IsPluginEnabled(options) && options.Enhance.EnhanceChineseSearch,
                        IsPluginEnabled(options) && options.Enhance.EnhanceChineseSearchRestore,
                        options.Enhance.SearchScope,
                        options.Enhance.ExcludeOriginalTitleFromSearch);
                },
                IsEnabled = options =>
                    IsPluginEnabled(options) &&
                    (options.Enhance.EnhanceChineseSearch || options.Enhance.EnhanceChineseSearchRestore),
                IsReady = () => ChineseSearch.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "StrmVideoDirectRedirect",
                Initialize = options => StrmVideoDirectRedirect.Initialize(
                    logger,
                    options.Enhance.EnableStrmDirectRedirect,
                    options.Enhance.StrmDirectRedirectFollow302,
                    options.Enhance.StrmVideoDirectRedirectClientBlacklist),
                Configure = options => StrmVideoDirectRedirect.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnableStrmDirectRedirect,
                    options.Enhance.StrmDirectRedirectFollow302,
                    options.Enhance.StrmVideoDirectRedirectClientBlacklist),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableStrmDirectRedirect,
                IsReady = () => StrmVideoDirectRedirect.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "StrmAudioDirectRedirect",
                Initialize = options => StrmAudioDirectRedirect.Initialize(
                    logger,
                    options.Enhance.EnableStrmDirectRedirect,
                    options.Enhance.StrmDirectRedirectFollow302,
                    options.Enhance.StrmAudioDirectRedirectClientBlacklist),
                Configure = options => StrmAudioDirectRedirect.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnableStrmDirectRedirect,
                    options.Enhance.StrmDirectRedirectFollow302,
                    options.Enhance.StrmAudioDirectRedirectClientBlacklist),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableStrmDirectRedirect,
                IsReady = () => StrmAudioDirectRedirect.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "DeepDelete",
                Initialize = options => DeepDelete.Initialize(logger, options.Enhance.EnableDeepDelete),
                Configure = options => DeepDelete.Configure(IsPluginEnabled(options) && options.Enhance.EnableDeepDelete),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableDeepDelete,
                IsReady = () => DeepDelete.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "NfoMetadataEnhance",
                Initialize = options => NfoMetadataEnhance.Initialize(logger, options.Enhance.EnableNfoMetadataEnhance),
                Configure = options => NfoMetadataEnhance.Configure(IsPluginEnabled(options) && options.Enhance.EnableNfoMetadataEnhance),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableNfoMetadataEnhance,
                IsReady = () => NfoMetadataEnhance.IsReady,
                IsWaiting = () => NfoMetadataEnhance.IsWaiting,
                Notes = () => NfoMetadataEnhance.IsWaiting ? "waiting for NfoMetadata assembly" : null
            });

            registrations.Add(new PatchRegistration
            {
                Name = "HidePersonNoImage",
                Initialize = options => HidePersonNoImage.Initialize(logger, options.Enhance.HidePersonNoImage),
                Configure = options => HidePersonNoImage.Configure(IsPluginEnabled(options) && options.Enhance.HidePersonNoImage),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.HidePersonNoImage,
                IsReady = () => HidePersonNoImage.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "SeriesTotalEpisodeCount",
                Initialize = options => SeriesTotalEpisodeCount.Initialize(
                    logger,
                    options.Enhance.EnableSeriesTotalEpisodeCount),
                Configure = options => SeriesTotalEpisodeCount.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnableSeriesTotalEpisodeCount),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableSeriesTotalEpisodeCount,
                IsReady = () => SeriesTotalEpisodeCount.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "PlaybackMediaSourceName",
                Initialize = options => PlaybackMediaSourceName.Initialize(
                    logger,
                    options.Enhance.EnablePlaybackMediaSourceName),
                Configure = options => PlaybackMediaSourceName.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnablePlaybackMediaSourceName),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnablePlaybackMediaSourceName,
                IsReady = () => PlaybackMediaSourceName.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "EpisodeBackdropFallback",
                Initialize = options => EpisodeBackdropFallback.Initialize(
                    logger,
                    options.Enhance.EnableEpisodeBackdropFallback,
                    options.Enhance.EnableEpisodeImageAspectRatioOptimize),
                Configure = options => EpisodeBackdropFallback.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnableEpisodeBackdropFallback,
                    IsPluginEnabled(options) && options.Enhance.EnableEpisodeImageAspectRatioOptimize),
                IsEnabled = options => IsPluginEnabled(options) &&
                    (options.Enhance.EnableEpisodeBackdropFallback || options.Enhance.EnableEpisodeImageAspectRatioOptimize),
                IsReady = () => EpisodeBackdropFallback.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "AudioAlbumPrimaryFallback",
                Initialize = options => AudioAlbumPrimaryFallback.Initialize(
                    logger,
                    options.Enhance.EnableAudioAlbumPrimaryFallback),
                Configure = options => AudioAlbumPrimaryFallback.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnableAudioAlbumPrimaryFallback),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableAudioAlbumPrimaryFallback,
                IsReady = () => AudioAlbumPrimaryFallback.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "PinyinSortName",
                Initialize = options => PinyinSortName.Initialize(logger, options.Enhance.EnablePinyinSortName),
                Configure = options => PinyinSortName.Configure(IsPluginEnabled(options) && options.Enhance.EnablePinyinSortName),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnablePinyinSortName,
                IsReady = () => PinyinSortName.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "NoBoxsetsAutoCreation",
                Initialize = options => NoBoxsetsAutoCreation.Initialize(logger, options.Enhance.NoBoxsetsAutoCreation),
                Configure = options => NoBoxsetsAutoCreation.Configure(IsPluginEnabled(options) && options.Enhance.NoBoxsetsAutoCreation),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.NoBoxsetsAutoCreation,
                IsReady = () => NoBoxsetsAutoCreation.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "EnforceLibraryOrder",
                Initialize = options => EnforceLibraryOrder.Initialize(logger, options.Enhance.EnforceLibraryOrder),
                Configure = options => EnforceLibraryOrder.Configure(IsPluginEnabled(options) && options.Enhance.EnforceLibraryOrder),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnforceLibraryOrder,
                IsReady = () => EnforceLibraryOrder.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "MergeMultiVersion",
                Initialize = options =>
                {
                    options.Enhance ??= new EnhanceOptions();
                    MergeMultiVersion.Initialize(
                        logger,
                        IsPluginEnabled(options) && options.Enhance.MergeMultiVersion,
                        options.Enhance.MergeSeriesPreference);
                },
                Configure = options =>
                {
                    options.Enhance ??= new EnhanceOptions();
                    MergeMultiVersion.Configure(
                        IsPluginEnabled(options) && options.Enhance.MergeMultiVersion,
                        options.Enhance.MergeSeriesPreference);
                },
                IsEnabled = options =>
                {
                    options.Enhance ??= new EnhanceOptions();
                    return IsPluginEnabled(options) && options.Enhance.MergeMultiVersion;
                },
                IsReady = () => MergeMultiVersion.IsReady,
                IsWaiting = () => false
            });

            registrations.Add(new PatchRegistration
            {
                Name = "LibrayProviderSettings",
                Initialize = options => LibrayProviderSettings.Initialize(
                    logger,
                    options.Enhance.EnableLibrayProviderSettings),
                Configure = options => LibrayProviderSettings.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnableLibrayProviderSettings),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableLibrayProviderSettings,
                IsReady = () => LibrayProviderSettings.IsReady,
                Notes = () => "prefer TheMovieDb defaults"
            });

            registrations.Add(new PatchRegistration
            {
                Name = "NotificationSystem",
                Initialize = _ => NotificationSystem.Initialize(logger),
                Configure = _ => { },
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableNotificationEnhance,
                IsReady = () => NotificationSystem.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "DashboardResourcePatch",
                Initialize = options => DashboardResourcePatch.Initialize(
                    logger,
                    IsPluginEnabled(options) && options.MetaData.EnableDanmakuJs),
                Configure = options => DashboardResourcePatch.Configure(
                    IsPluginEnabled(options) && options.MetaData.EnableDanmakuJs),
                IsEnabled = options => IsPluginEnabled(options) && options.MetaData.EnableDanmakuJs,
                IsReady = () => DashboardResourcePatch.IsReady
            });

            registrations.Add(new PatchRegistration
            {
                Name = "SystemLog",
                Initialize = options => SystemLog.Initialize(logger, true, options.Enhance.SystemLogNameBlacklist),
                Configure = options => SystemLog.Configure(IsPluginEnabled(options), options.Enhance.SystemLogNameBlacklist),
                IsEnabled = IsPluginEnabled,
                IsReady = () => SystemLog.IsReady,
                Notes = () => SystemLog.IsReady ? null : "NamedLogger.Log 未命中"
            });

            registrations.Add(new PatchRegistration
            {
                Name = "SystemLogReverse",
                Initialize = options => SystemLogReverse.Initialize(
                    logger,
                    IsPluginEnabled(options) && options.Enhance.EnableSystemLogReverse),
                Configure = options => SystemLogReverse.Configure(
                    IsPluginEnabled(options) && options.Enhance.EnableSystemLogReverse),
                IsEnabled = options => IsPluginEnabled(options) && options.Enhance.EnableSystemLogReverse,
                IsReady = () => SystemLogReverse.IsReady,
                Notes = () => SystemLogReverse.IsReady ? null : "SystemService.Get 日志接口未命中"
            });

            registrations.Add(new PatchRegistration
            {
                Name = "PluginUiTabTitle",
                Initialize = _ => PluginUiTabTitle.Initialize(logger),
                Configure = _ => PluginUiTabTitle.Configure(),
                IsEnabled = _ => true,
                IsReady = () => PluginUiTabTitle.IsReady,
                Notes = () => "always enabled"
            });
        }

        private static void RegisterTrackers()
        {
            foreach (var registration in registrations)
            {
                trackers.Add(new PatchTracker(registration.Name)
                {
                    Approach = registration.Approach
                });
            }
        }

        private static bool IsPluginEnabled(PluginConfiguration options)
        {
            return options?.MainPage?.PlugginEnabled ?? true;
        }

        private static PluginConfiguration EnsureOptions(PluginConfiguration options)
        {
            var safe = options ?? new PluginConfiguration();
            safe.MainPage ??= new MainPageOptions();
            safe.IntroSkip ??= new IntroSkipOptions();
            safe.GetNetWorkOptions();
            safe.Enhance ??= new EnhanceOptions();
            safe.MetaData ??= new MetaDataOptions();
#if DEBUG
            safe.Debug ??= new DebugOptions();
#endif
            return safe;
        }

        private static bool GetFfProcessGuardEnabled(PluginConfiguration options)
        {
#if DEBUG
            return options?.Debug?.EnableFfProcessGuard ?? true;
#else
            return true;
#endif
        }

        private static bool HasNetworkServerFeatures(PluginConfiguration options)
        {
            var networkOptions = options?.GetNetWorkOptions();
            if (networkOptions == null)
            {
                return false;
            }

            return networkOptions.EnableProxyServer ||
                   networkOptions.EnableGzip ||
                   !string.IsNullOrWhiteSpace(networkOptions.AlternativeTmdbApiUrl) ||
                   !string.IsNullOrWhiteSpace(networkOptions.AlternativeTmdbImageUrl) ||
                   !string.IsNullOrWhiteSpace(networkOptions.AlternativeTmdbApiKey);
        }

        private static string BuildProxyNotes()
        {
            if (!NetworkServer.IsReady)
            {
                return "CreateHttpClientHandler 未命中";
            }

            return NetworkServer.IsHttpClientHookReady ? null : "HttpClientManager hook not ready";
        }

        private static string BuildLocalDiscoveryNotes()
        {
            var options = Plugin.Instance?.Options?.GetNetWorkOptions();
            var configuredValue = LocalDiscoveryAddress.NormalizeConfiguredValue(options?.CustomLocalDiscoveryAddress);
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return null;
            }

            if (string.Equals(configuredValue, "BLOCKED", StringComparison.Ordinal))
            {
                return LocalDiscoveryAddress.IsUdpBlockReady ? "udp blocked" : "RespondToMessage 未命中";
            }

            if (LocalDiscoveryAddress.IsHttpReady && LocalDiscoveryAddress.IsUdpRewriteReady)
            {
                return "configured custom address";
            }

            if (LocalDiscoveryAddress.IsHttpReady && !LocalDiscoveryAddress.IsUdpRewriteReady)
            {
                return "http-only active";
            }

            if (!LocalDiscoveryAddress.IsHttpReady)
            {
                return "GetPublicSystemInfo/GetSystemInfo 未完全命中";
            }

            return "SendMessage/RespondToMessage 未完全命中";
        }

        private static void LogTrackerSummary()
        {
            if (!trackers.Any())
            {
                return;
            }

            var enabledCount = trackers.Count(t => t.Health == PatchHealth.Enabled);
            var disabledCount = trackers.Count(t => t.Health == PatchHealth.Disabled);
            var waitingCount = trackers.Count(t => t.Health == PatchHealth.Waiting);
            var failedCount = trackers.Count(t => t.Health == PatchHealth.Failed);

            logger?.Info(
                "补丁加载摘要：启用={0}，禁用={1}，等待={2}，失败={3}",
                enabledCount,
                disabledCount,
                waitingCount,
                failedCount);

            foreach (var tracker in trackers)
            {
                if (tracker.Health == PatchHealth.Failed)
                {
                    logger?.Warn(
                        "补丁状态：{0}=失败{1}",
                        tracker.Name,
                        string.IsNullOrWhiteSpace(tracker.Notes) ? string.Empty : " (" + tracker.Notes + ")");
                    continue;
                }

                if (tracker.Health == PatchHealth.Waiting)
                {
                    logger?.Info(
                        "补丁状态：{0}=等待{1}",
                        tracker.Name,
                        string.IsNullOrWhiteSpace(tracker.Notes) ? string.Empty : " (" + tracker.Notes + ")");
                    continue;
                }

                if (tracker.Health == PatchHealth.Disabled)
                {
                    logger?.Debug("补丁状态：{0}=禁用", tracker.Name);
                }
            }
        }

        private static void ReportFailedEnabledPatches()
        {
            if (activityManager == null)
            {
                return;
            }

            var failedEnabledTrackers = trackers
                .Where(t => t.IsEnabled && t.Health == PatchHealth.Failed)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();

            if (failedEnabledTrackers.Length == 0)
            {
                lastReportedFailureKey = null;
                return;
            }

            var failureKey = string.Join("|", failedEnabledTrackers.Select(t => t.Name + ":" + (t.Notes ?? string.Empty)));
            if (string.Equals(lastReportedFailureKey, failureKey, StringComparison.Ordinal))
            {
                return;
            }

            lastReportedFailureKey = failureKey;

            var failedPatchNames = string.Join(
                "，",
                failedEnabledTrackers.Select(t => t.Name));

            activityManager.Create(new ActivityLogEntry
            {
                Name = Plugin.PluginName + " Patch 失效：" + failedPatchNames,
                Type = "PluginPatchHealthFailed",
                Severity = LogSeverity.Error
            });
        }
    }
}
