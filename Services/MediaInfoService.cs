using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Patch;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper.Services
{
    public class MediaInfoService
    {
        private const string MediaInfoFileExtension = "-mediainfo.json";

        public enum MediaInfoRestoreResult
        {
            Restored,
            AlreadyExists,
            Failed
        }

        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;
        private readonly IMediaMountManager mediaMountManager;

        internal class MediaSourceWithChapters
        {
            public MediaSourceInfo MediaSourceInfo { get; set; }
            public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
        }

        /// <summary>创建 MediaInfo 处理辅助类并注入所需服务。</summary>
        public MediaInfoService(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IMediaMountManager mediaMountManager)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
            this.itemRepository = itemRepository;
            this.jsonSerializer = jsonSerializer;
            this.mediaMountManager = mediaMountManager;
        }

        /// <summary>构建 MediaInfo 提取所需的刷新选项。</summary>
        public MetadataRefreshOptions GetMediaInfoRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        /// <summary>为片头扫描执行最小刷新，确保条目进入可扫描状态。</summary>
        public async Task<Episode> TryRefreshEpisodeForIntroScanAsync(Episode episode, string source)
        {
            if (episode == null)
            {
                return null;
            }

            try
            {
                var metadataRefreshOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages = false,
                    EnableThumbnailImageExtraction = false,
                    EnableSubtitleDownloading = false
                };
                var collectionFolders = this.libraryManager.GetCollectionFolders(episode).Cast<BaseItem>().ToArray();
                var libraryOptions = this.libraryManager.GetLibraryOptions(episode);
                using (FfprobeGuard.Allow())
                {
                    episode.DateLastRefreshed = new DateTimeOffset();
                    await Plugin.ProviderManager
                        .RefreshSingleItem(episode, metadataRefreshOptions, collectionFolders, libraryOptions, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                if (Plugin.LibraryService.HasMediaInfo(episode))
                {
                    await SerializeMediaInfo(
                            episode.InternalId,
                            new DirectoryService(this.logger, this.fileSystem),
                            true,
                            source + " PersistAfterRefresh")
                        .ConfigureAwait(false);
                }

                return episode;
            }
            catch (Exception ex)
            {
                this.logger.Error($"{source} 片头扫描: 未刷新条目触发刷新失败 {episode.Path} InternalId: {episode.InternalId}");
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
                return null;
            }
        }

        /// <summary>片头扫描前预提取：执行挂载解析、恢复与探测。</summary>
        public async Task<Episode> PrepareEpisodeForIntroScanAsync(Episode episode, IDirectoryService directoryService, string source)
        {
            if (episode == null)
            {
                return null;
            }

            var workEpisode = this.libraryManager.GetItemById(episode.InternalId) as Episode ?? episode;
            var ds = directoryService ?? new DirectoryService(this.logger, this.fileSystem);

            if (workEpisode.IsShortcut)
            {
                var mountedPath = await GetStrmMountPathAsync(workEpisode.Path).ConfigureAwait(false);
                if (string.IsNullOrEmpty(mountedPath))
                {
                    this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} InternalId: {workEpisode.InternalId} 挂载路径解析失败，跳过扫描");
                    return null;
                }
            }

            var restoreSucceeded = false;
            if (!Plugin.LibraryService.HasMediaInfo(workEpisode))
            {
                logger.Info($"{source} 片头扫描预提取: {workEpisode.FileName} 无 MediaInfo，尝试从 JSON 恢复");
                var restoreResult = await DeserializeMediaInfo(workEpisode, ds, source + " Restore")
                    .ConfigureAwait(false);
                restoreSucceeded = restoreResult == MediaInfoRestoreResult.Restored ||
                                  restoreResult == MediaInfoRestoreResult.AlreadyExists;

                if (!restoreSucceeded)
                {
                    this.logger.Info($"{source} 片头扫描预提取: {workEpisode.FileName} 开始提取媒体信息");
                    workEpisode = await TryRefreshEpisodeForIntroScanAsync(workEpisode, source + " Extract")
                        .ConfigureAwait(false);
                    if (workEpisode == null)
                    {
                        return null;
                    }
                }
            }

            workEpisode = this.libraryManager.GetItemById(workEpisode.InternalId) as Episode ?? workEpisode;
            if (!Plugin.LibraryService.HasMediaInfo(workEpisode))
            {
                this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} 提取后仍无 MediaInfo，跳过扫描");
                return null;
            }

            var hasAudioStream = workEpisode.GetMediaStreams().Any(s => s.Type == MediaStreamType.Audio);
            if (!hasAudioStream)
            {
                this.logger.Warn($"{source} 片头扫描预提取: {workEpisode.FileName} MediaInfo 存在但无音频流，跳过扫描");
                return null;
            }

            return workEpisode;
        }

        private async Task<string> GetStrmMountPathAsync(string strmPath)
        {
            if (string.IsNullOrWhiteSpace(strmPath))
            {
                return null;
            }

            if (this.mediaMountManager == null)
            {
                this.logger.Warn("片头扫描预提取: IMediaMountManager 为空，无法解析 strm 挂载路径");
                return null;
            }

            try
            {
                using var mediaMount = await this.mediaMountManager.Mount(strmPath, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return mediaMount?.MountedPathInfo?.FullName;
            }
            catch (Exception ex)
            {
                this.logger.Warn($"片头扫描预提取: strm 挂载路径解析异常 {strmPath}");
                this.logger.Warn(ex.Message);
                return null;
            }
        }

        /// <summary>根据配置计算媒体条目的 JSON 保存路径。</summary>
        public static string GetMediaInfoJsonPath(BaseItem item)
        {
            var jsonRootFolder = Plugin.Instance.Options.MainPage.MediaInfoJsonRootFolder?.Trim();

            var mediaInfoFileName = GetMediaInfoFileName(item);
            var mediaInfoJsonPath = !string.IsNullOrWhiteSpace(jsonRootFolder)
                ? Path.Combine(jsonRootFolder, mediaInfoFileName)
                : Path.Combine(item.ContainingFolderPath, mediaInfoFileName);

            return mediaInfoJsonPath;
        }

        private static string GetMediaInfoFileName(BaseItem item)
        {
            return item.FileNameWithoutExtension + MediaInfoFileExtension;
        }
        
        private static bool IsValidTmdbId(string tmdbId)
        {
            return !string.IsNullOrWhiteSpace(tmdbId) &&
                   !string.Equals(tmdbId, "None", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetTmdbId(BaseItem item, out string tmdbId)
        {
            tmdbId = null;

            if (item is Series seriesItem)
            {
                tmdbId = seriesItem.GetProviderId(MetadataProviders.Tmdb);
                return IsValidTmdbId(tmdbId);
            }

            if (item is Episode episodeWithSeries)
            {
                if (Plugin.LibraryManager == null)
                {
                    return false;
                }

                var series = Plugin.LibraryManager.GetItemById(episodeWithSeries.SeriesId) as Series;
                tmdbId = series?.GetProviderId(MetadataProviders.Tmdb);
                return IsValidTmdbId(tmdbId);
            }

            if (item is Season season)
            {
                if (Plugin.LibraryManager == null)
                {
                    return false;
                }

                var series = Plugin.LibraryManager.GetItemById(season.ParentId) as Series;
                tmdbId = series?.GetProviderId(MetadataProviders.Tmdb);
                return IsValidTmdbId(tmdbId);
            }

            tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            return IsValidTmdbId(tmdbId);
        }

        /// <summary>将媒体条目的 MediaInfo 与章节序列化到 JSON。</summary>
        private bool SerializeMediaInfo(BaseItem item, IDirectoryService directoryService, bool overwrite,
            string source)
        {
            if (!TryGetTmdbId(item, out var tmdbId))
            {
                this.logger.Info($"{source} 跳过保存 - 无 TMDB ID: {item.FileName ?? item.Path ?? item.Id.ToString()}");
                return false;
            }

            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (overwrite || file?.Exists != true)
            {
                try
                {
                    var options = this.libraryManager.GetLibraryOptions(item);
                    var mediaSources = item.GetMediaSources(false, false, options);
                    var chapters = this.itemRepository.GetChapters(item);
                    var mediaSourcesWithChapters = mediaSources.Select(mediaSource =>
                            new MediaSourceWithChapters { MediaSourceInfo = mediaSource, Chapters = chapters })
                        .ToList();

                    foreach (var jsonItem in mediaSourcesWithChapters)
                    {
                        jsonItem.MediaSourceInfo.Id = null;
                        jsonItem.MediaSourceInfo.ItemId = null;
                        jsonItem.MediaSourceInfo.Path = null;

                        foreach (var subtitle in jsonItem.MediaSourceInfo.MediaStreams.Where(m =>
                                     m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                                     m.Protocol == MediaProtocol.File))
                        {
                            subtitle.Path = this.fileSystem.GetFileInfo(subtitle.Path).Name;
                        }

                        foreach (var chapter in jsonItem.Chapters)
                        {
                            chapter.ImageTag = null;
                        }
                    }

                    var parentDirectory = Path.GetDirectoryName(mediaInfoJsonPath);
                    if (!string.IsNullOrEmpty(parentDirectory))
                    {
                        Directory.CreateDirectory(parentDirectory);
                    }

                    this.jsonSerializer.SerializeToFile(mediaSourcesWithChapters, mediaInfoJsonPath);
                    this.logger.Info($"{source} 保存成功: {item.FileName ?? item.Path} {mediaInfoJsonPath}");

                    return true;
                }
                catch (Exception e)
                {
                    this.logger.Error($"{source} 保存失败: {item.FileName ?? item.Path} {mediaInfoJsonPath}");
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        /// <summary>序列化指定条目 ID 的 MediaInfo。</summary>
        public Task<bool> SerializeMediaInfo(long itemId, IDirectoryService directoryService, bool overwrite,
            string source)
        {
            var workItem = this.libraryManager.GetItemById(itemId);

            if (!Plugin.LibraryService.HasMediaInfo(workItem))
            {
                this.logger.Info($"{source} 跳过保存 - 无 MediaInfo");
                return Task.FromResult(false);
            }

            var ds = directoryService ?? new DirectoryService(this.logger, this.fileSystem);
            return Task.FromResult(SerializeMediaInfo(workItem, ds, overwrite, source));
        }

        /// <summary>从 JSON 恢复 MediaInfo，并在有效时更新条目。</summary>
        public async Task<MediaInfoRestoreResult> DeserializeMediaInfo(BaseItem item, IDirectoryService directoryService, string source)
        {
            var workItem = this.libraryManager.GetItemById(item.InternalId);
            if (workItem == null)
            {
                this.logger.Info($"{source} 跳过恢复: 条目不存在 InternalId={item.InternalId}");
                return MediaInfoRestoreResult.Failed;
            }

            if (Plugin.LibraryService.HasMediaInfo(workItem))
            {
                return MediaInfoRestoreResult.AlreadyExists;
            }

            var ds = directoryService ?? new DirectoryService(this.logger, this.fileSystem);
            var mediaInfoJsonPath = GetMediaInfoJsonPath(workItem);
            var file = ds.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    var mediaSourceWithChapters = (await this.jsonSerializer
                            .DeserializeFromFileAsync<List<MediaSourceWithChapters>>(mediaInfoJsonPath)
                            .ConfigureAwait(false))
                        .ToArray()[0];

                    if (mediaSourceWithChapters?.MediaSourceInfo?.RunTimeTicks.HasValue is true)
                    {
                        foreach (var subtitle in mediaSourceWithChapters.MediaSourceInfo.MediaStreams.Where(m =>
                                     m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                                     m.Protocol == MediaProtocol.File))
                        {
                            subtitle.Path = Path.Combine(workItem.ContainingFolderPath,
                                this.fileSystem.GetFileInfo(subtitle.Path).Name);
                        }

                        this.itemRepository.SaveMediaStreams(item.InternalId,
                            mediaSourceWithChapters.MediaSourceInfo.MediaStreams, CancellationToken.None);

                        workItem.Size = mediaSourceWithChapters.MediaSourceInfo.Size.GetValueOrDefault();
                        workItem.RunTimeTicks = mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks;
                        workItem.Container = mediaSourceWithChapters.MediaSourceInfo.Container;
                        workItem.TotalBitrate = mediaSourceWithChapters.MediaSourceInfo.Bitrate.GetValueOrDefault();

                        var videoStream = mediaSourceWithChapters.MediaSourceInfo.MediaStreams
                            .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                            .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                            .FirstOrDefault();

                        if (videoStream != null)
                        {
                            workItem.Width = videoStream.Width.GetValueOrDefault();
                            workItem.Height = videoStream.Height.GetValueOrDefault();
                        }

                        this.libraryManager.UpdateItems(new List<BaseItem> { workItem }, null,
                            ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                        if (workItem is Video)
                        {
                            this.itemRepository.SaveChapters(item.InternalId, true, mediaSourceWithChapters.Chapters);
                        }

                        this.logger.Info($"{source} 恢复成功: {workItem.FileName ?? workItem.Path}");

                        return MediaInfoRestoreResult.Restored;
                    }

                    this.logger.Info($"{source} 跳过恢复: {workItem.FileName ?? workItem.Path}");
                }
                catch (Exception e)
                {
                    this.logger.Error($"{source} 恢复失败: {workItem.FileName ?? workItem.Path}");
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }
            }
            else
            {
                this.logger.Info($"{source} 未找到 JSON: {workItem.FileName ?? workItem.Path} {mediaInfoJsonPath}");
            }

            return MediaInfoRestoreResult.Failed;
        }

        /// <summary>删除媒体条目的已持久化 JSON。</summary>
        public void DeleteMediaInfoJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists is true)
            {
                try
                {
                    this.logger.Info($"MediaInfoKeeper {source} 尝试删除: {item.FileName ?? item.Path} {mediaInfoJsonPath}");
                    this.fileSystem.DeleteFile(mediaInfoJsonPath);
                }
                catch (Exception e)
                {
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }
            }
            else
            {
                this.logger.Info($"MediaInfoKeeper {source} 未找到JSON: {item.FileName ?? item.Path} {mediaInfoJsonPath}");
            }
        }

        /// <summary>获取指定条目的静态媒体源。</summary>
        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            var options = this.libraryManager.GetLibraryOptions(item);
            return item.GetMediaSources(enableAlternateMediaSources, false, options);
        }

        /// <summary>生成相对路径并处理基础兼容逻辑。</summary>
        private static string GetRelativePathCompat(string rootPath, string fullPath)
        {
            if (string.IsNullOrEmpty(rootPath) || string.IsNullOrEmpty(fullPath))
            {
                return fullPath;
            }

            var root = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;

            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length)
                : fullPath;
        }
    }
}
