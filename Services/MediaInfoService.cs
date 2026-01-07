using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
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

        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;

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
            IJsonSerializer jsonSerializer)
        {
            this.logger = Plugin.Instance.Logger;
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
            this.itemRepository = itemRepository;
            this.jsonSerializer = jsonSerializer;
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

        /// <summary>根据配置计算媒体条目的 JSON 保存路径。</summary>
        public static string GetMediaInfoJsonPath(BaseItem item)
        {
            var jsonRootFolder = Plugin.Instance.Options.MediaInfoJsonRootFolder;

            var relativePath = item.ContainingFolderPath;
            if (!string.IsNullOrEmpty(jsonRootFolder) && Path.IsPathRooted(item.ContainingFolderPath))
            {
                relativePath = GetRelativePathCompat(Path.GetPathRoot(item.ContainingFolderPath),
                    item.ContainingFolderPath);
            }

            var mediaInfoJsonPath = !string.IsNullOrEmpty(jsonRootFolder)
                ? Path.Combine(jsonRootFolder, relativePath, item.FileNameWithoutExtension + MediaInfoFileExtension)
                : Path.Combine(item.ContainingFolderPath, item.FileNameWithoutExtension + MediaInfoFileExtension);

            return mediaInfoJsonPath;
        }

        /// <summary>将媒体条目的 MediaInfo 与章节序列化到 JSON。</summary>
        private bool SerializeMediaInfo(BaseItem item, IDirectoryService directoryService, bool overwrite,
            string source)
        {
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
                    this.logger.Info($"MediaInfoKeeper {source} 保存成功: {mediaInfoJsonPath}");

                    return true;
                }
                catch (Exception e)
                {
                    this.logger.Error($"MediaInfoKeeper {source} 保存失败: {mediaInfoJsonPath}");
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
                this.logger.Info($"MediaInfoKeeper {source} 跳过保存 - 无 MediaInfo");
                return Task.FromResult(false);
            }

            var ds = directoryService ?? new DirectoryService(this.logger, this.fileSystem);
            return Task.FromResult(SerializeMediaInfo(workItem, ds, overwrite, source));
        }

        /// <summary>从 JSON 恢复 MediaInfo，并在有效时更新条目。</summary>
        public async Task<bool> DeserializeMediaInfo(BaseItem item, IDirectoryService directoryService, string source,
            bool ignoreFileChange)
        {
            var workItem = this.libraryManager.GetItemById(item.InternalId);

            if (Plugin.LibraryService.HasMediaInfo(workItem))
            {
                return true;
            }

            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    var mediaSourceWithChapters = (await this.jsonSerializer
                            .DeserializeFromFileAsync<List<MediaSourceWithChapters>>(mediaInfoJsonPath)
                            .ConfigureAwait(false))
                        .ToArray()[0];

                    if (mediaSourceWithChapters?.MediaSourceInfo?.RunTimeTicks.HasValue is true &&
                        (ignoreFileChange || !Plugin.LibraryService.HasFileChanged(item, directoryService)))
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

                        this.logger.Info($"MediaInfoKeeper {source} 恢复成功: {mediaInfoJsonPath}");

                        return true;
                    }

                    this.logger.Info($"MediaInfoKeeper {source} 跳过恢复: {mediaInfoJsonPath}");
                }
                catch (Exception e)
                {
                    this.logger.Error($"MediaInfoKeeper {source} 恢复失败: {mediaInfoJsonPath}");
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }
            }

            return false;
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
                    this.logger.Info($"MediaInfoKeeper {source} 尝试删除: {mediaInfoJsonPath}");
                    this.fileSystem.DeleteFile(mediaInfoJsonPath);
                }
                catch (Exception e)
                {
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }
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









