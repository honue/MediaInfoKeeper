using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;

namespace MediaInfoKeeper.Services
{
    public class LibraryService
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;

        /// <summary>创建库处理辅助类并注入所需服务。</summary>
        public LibraryService(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
        }

        /// <summary>判断条目是否已存在 MediaInfo。</summary>
        public bool HasMediaInfo(BaseItem item)
        {
            if (!item.RunTimeTicks.HasValue)
            {
                return false;
            }

            if (item.Size == 0)
            {
                return false;
            }

            return item.GetMediaStreams().Any(i => i.Type == MediaStreamType.Video || i.Type == MediaStreamType.Audio);
        }

        /// <summary>检测底层文件是否发生变化。</summary>
        public bool HasFileChanged(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IsFileProtocol)
            {
                var file = directoryService.GetFile(item.Path);
                if (file != null && item.HasDateModifiedChanged(file.LastWriteTimeUtc))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 根据配置与条目状态编排恢复、刷新与持久化流程。
        /// </summary>
        public async Task<bool?> OrchestrateMediaInfoProcessAsync(BaseItem taskItem, string source,
            CancellationToken cancellationToken)
        {
            var displayName = taskItem.Path ?? taskItem.Name;

            if (!IsItemInScope(taskItem))
            {
                logger.Info($"跳过 不在库范围: {displayName}");
                return null;
            }

            var persistMediaInfo = taskItem is Video && Plugin.Instance.Options.PersistMediaInfoEnabled;
            if (!persistMediaInfo)
            {
                logger.Info($"跳过 未开启持久化或非视频: {displayName}");
                return null;
            }

            using (FfprobeGuard.Allow())
            using (MetadataProvidersGuard.Allow())
            {
                var filePath = taskItem.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    logger.Info($"跳过 无路径: {displayName}");
                    return null;
                }

                var refreshOptions = Plugin.MediaInfoService.GetMediaInfoRefreshOptions();
                var directoryService = refreshOptions.DirectoryService;

                if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                    uri.Scheme == Uri.UriSchemeFile)
                {
                    var file = directoryService.GetFile(filePath);
                    if (file?.Exists != true)
                    {
                        logger.Info($"跳过 文件不存在: {displayName}");
                        return null;
                    }
                }

                var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(taskItem);
                var libraryOptions = this.libraryManager.GetLibraryOptions(taskItem);

                var dummyLibraryOptions = CopyLibraryOptions(libraryOptions);
                dummyLibraryOptions.DisabledLocalMetadataReaders = new[] { "Nfo" };
                dummyLibraryOptions.MetadataSavers = Array.Empty<string>();

                foreach (var option in dummyLibraryOptions.TypeOptions)
                {
                    option.MetadataFetchers = Array.Empty<string>();
                    option.ImageFetchers = Array.Empty<string>();
                }

                if (persistMediaInfo)
                {
                    var deserializeResult = await Plugin.MediaInfoService
                        .DeserializeMediaInfo(taskItem, directoryService, source, false)
                        .ConfigureAwait(false);

                    if (deserializeResult)
                    {
                        logger.Info($"命中 JSON 恢复: {displayName}");
                        return false;
                    }
                }

                logger.Info($"刷新开始: {displayName}");
                taskItem.DateLastRefreshed = new DateTimeOffset();

                await this.providerManager
                    .RefreshSingleItem(taskItem, refreshOptions, collectionFolders, dummyLibraryOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (persistMediaInfo)
                {
                    logger.Info($"写入 JSON: {displayName}");
                    await Plugin.MediaInfoService.SerializeMediaInfo(taskItem.InternalId, directoryService, true, source)
                        .ConfigureAwait(false);
                }

                logger.Info($"完成: {displayName}");
                return true;
            }
        }

        /// <summary>根据配置判断条目是否属于选定媒体库。</summary>
        public bool IsItemInScope(BaseItem item)
        {
            var scopedLibraries = GetScopedLibraryKeys();
            if (scopedLibraries.Count == 0)
            {
                return true;
            }

            foreach (var collectionFolder in this.libraryManager.GetCollectionFolders(item))
            {
                if (collectionFolder == null)
                {
                    continue;
                }

                var name = collectionFolder.Name?.Trim();
                if (!string.IsNullOrEmpty(name) &&
                    scopedLibraries.Contains(name))
                {
                    return true;
                }

                if (scopedLibraries.Contains(collectionFolder.InternalId.ToString()))
                {
                    return true;
                }

                var id = collectionFolder.Id.ToString();
                if (scopedLibraries.Contains(id))
                {
                    return true;
                }
            }

            return false;
        }

        private HashSet<string> GetScopedLibraryKeys()
        {
            var raw = Plugin.Instance.Options.CatchupLibraries;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var tokens = raw
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrEmpty(value));

            return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>复制库配置，用于元数据刷新流程。</summary>
        public static LibraryOptions CopyLibraryOptions(LibraryOptions sourceOptions)
        {
            var targetOptions = new LibraryOptions
            {
                EnableArchiveMediaFiles = sourceOptions.EnableArchiveMediaFiles,
                EnablePhotos = sourceOptions.EnablePhotos,
                EnableRealtimeMonitor = sourceOptions.EnableRealtimeMonitor,
                EnableMarkerDetection = sourceOptions.EnableMarkerDetection,
                EnableMarkerDetectionDuringLibraryScan = sourceOptions.EnableMarkerDetectionDuringLibraryScan,
                IntroDetectionFingerprintLength = sourceOptions.IntroDetectionFingerprintLength,
                EnableChapterImageExtraction = sourceOptions.EnableChapterImageExtraction,
                ExtractChapterImagesDuringLibraryScan = sourceOptions.ExtractChapterImagesDuringLibraryScan,
                DownloadImagesInAdvance = sourceOptions.DownloadImagesInAdvance,
                CacheImages = sourceOptions.CacheImages,
                PathInfos =
                    sourceOptions.PathInfos?.Select(p => new MediaPathInfo
                    {
                        Path = p.Path,
                        NetworkPath = p.NetworkPath,
                        Username = p.Username,
                        Password = p.Password
                    })
                        .ToArray() ?? Array.Empty<MediaPathInfo>(),
                IgnoreHiddenFiles = sourceOptions.IgnoreHiddenFiles,
                IgnoreFileExtensions =
                    sourceOptions.IgnoreFileExtensions?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLocalMetadata = sourceOptions.SaveLocalMetadata,
                SaveMetadataHidden = sourceOptions.SaveMetadataHidden,
                SaveLocalThumbnailSets = sourceOptions.SaveLocalThumbnailSets,
                ImportPlaylists = sourceOptions.ImportPlaylists,
                EnableAutomaticSeriesGrouping = sourceOptions.EnableAutomaticSeriesGrouping,
                ShareEmbeddedMusicAlbumImages = sourceOptions.ShareEmbeddedMusicAlbumImages,
                EnableEmbeddedTitles = sourceOptions.EnableEmbeddedTitles,
                EnableAudioResume = sourceOptions.EnableAudioResume,
                AutoGenerateChapters = sourceOptions.AutoGenerateChapters,
                AutomaticRefreshIntervalDays = sourceOptions.AutomaticRefreshIntervalDays,
                PlaceholderMetadataRefreshIntervalDays = sourceOptions.PlaceholderMetadataRefreshIntervalDays,
                PreferredMetadataLanguage = sourceOptions.PreferredMetadataLanguage,
                PreferredImageLanguage = sourceOptions.PreferredImageLanguage,
                ContentType = sourceOptions.ContentType,
                MetadataCountryCode = sourceOptions.MetadataCountryCode,
                MetadataSavers = sourceOptions.MetadataSavers?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledLocalMetadataReaders =
                    sourceOptions.DisabledLocalMetadataReaders?.Clone() as string[] ?? Array.Empty<string>(),
                LocalMetadataReaderOrder = sourceOptions.LocalMetadataReaderOrder?.Clone() as string[] ?? null,
                DisabledLyricsFetchers =
                    sourceOptions.DisabledLyricsFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SaveLyricsWithMedia = sourceOptions.SaveLyricsWithMedia,
                LyricsDownloadMaxAgeDays = sourceOptions.LyricsDownloadMaxAgeDays,
                LyricsFetcherOrder = sourceOptions.LyricsFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                LyricsDownloadLanguages =
                    sourceOptions.LyricsDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                DisabledSubtitleFetchers =
                    sourceOptions.DisabledSubtitleFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleFetcherOrder =
                    sourceOptions.SubtitleFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                SkipSubtitlesIfEmbeddedSubtitlesPresent = sourceOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent,
                SkipSubtitlesIfAudioTrackMatches = sourceOptions.SkipSubtitlesIfAudioTrackMatches,
                SubtitleDownloadLanguages =
                    sourceOptions.SubtitleDownloadLanguages?.Clone() as string[] ?? Array.Empty<string>(),
                SubtitleDownloadMaxAgeDays = sourceOptions.SubtitleDownloadMaxAgeDays,
                RequirePerfectSubtitleMatch = sourceOptions.RequirePerfectSubtitleMatch,
                SaveSubtitlesWithMedia = sourceOptions.SaveSubtitlesWithMedia,
                ForcedSubtitlesOnly = sourceOptions.ForcedSubtitlesOnly,
                HearingImpairedSubtitlesOnly = sourceOptions.HearingImpairedSubtitlesOnly,
                CollapseSingleItemFolders = sourceOptions.CollapseSingleItemFolders,
                EnableAdultMetadata = sourceOptions.EnableAdultMetadata,
                ImportCollections = sourceOptions.ImportCollections,
                MinCollectionItems = sourceOptions.MinCollectionItems,
                MusicFolderStructure = sourceOptions.MusicFolderStructure,
                MinResumePct = sourceOptions.MinResumePct,
                MaxResumePct = sourceOptions.MaxResumePct,
                MinResumeDurationSeconds = sourceOptions.MinResumeDurationSeconds,
                ThumbnailImagesIntervalSeconds = sourceOptions.ThumbnailImagesIntervalSeconds,
                SampleIgnoreSize = sourceOptions.SampleIgnoreSize,
                TypeOptions = sourceOptions.TypeOptions.Select(t => new TypeOptions
                {
                    Type = t.Type,
                    MetadataFetchers = t.MetadataFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                    MetadataFetcherOrder = t.MetadataFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                    ImageFetchers = t.ImageFetchers?.Clone() as string[] ?? Array.Empty<string>(),
                    ImageFetcherOrder = t.ImageFetcherOrder?.Clone() as string[] ?? Array.Empty<string>(),
                    ImageOptions = t.ImageOptions?.Select(i =>
                            new ImageOption { Type = i.Type, Limit = i.Limit, MinWidth = i.MinWidth })
                            .ToArray() ?? Array.Empty<ImageOption>()
                })
                    .ToArray()
            };

            return targetOptions;
        }
    }
}
