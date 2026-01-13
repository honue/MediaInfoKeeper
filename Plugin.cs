using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using Emby.Web.GenericEdit.Common;
using MediaInfoKeeper.Configuration;
using MediaInfoKeeper.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace MediaInfoKeeper
{
    /// <summary>
    /// The plugin.
    /// </summary>
    public class Plugin : BasePluginSimpleUI<PluginConfiguration>, IHasThumbImage
    {
        public const string PluginName = "MediaInfoKeeper";

        public static Plugin Instance { get; private set; }
        public static MediaInfoService MediaInfoService { get; private set; }
        public static LibraryService LibraryService { get; private set; }

        private readonly Guid id = new Guid("874D7056-072D-43A4-16DD-BC32665B9563");
        private readonly ILogger logger;

        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;

        internal static IProviderManager ProviderManager { get; private set; }
        internal static IFileSystem FileSystem { get; private set; }

        private bool currentPersistMediaInfo;
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        private static readonly object LatestVersionLock = new object();
        private static DateTimeOffset latestVersionCheckedUtc = DateTimeOffset.MinValue;
        private static string latestReleaseVersionCache;
        private static readonly TimeSpan LatestVersionCacheDuration = TimeSpan.FromMinutes(30);
        private const string GitHubLatestReleaseUrl = "https://api.github.com/repos/honue/MediaInfoKeeper/releases/latest";

        /// <summary>初始化插件并注册库事件处理。</summary>
        public Plugin(
            IApplicationHost applicationHost,
            ILogManager logManager,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            IFileSystem fileSystem) : base(applicationHost)
        {
            Instance = this;
            this.logger = logManager.GetLogger(this.Name);
            this.logger.Info($"插件 {this.Name} 正在加载");

            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            ProviderManager = providerManager;
            FileSystem = fileSystem;

            FfprobeGuard.Initialize(this.logger, this.Options.General.DisableSystemFfprobe);
            MetadataProvidersWatcher.Initialize(this.logger, this.Options.General.EnableMetadataProvidersWatcher);

            this.currentPersistMediaInfo = this.Options.General.PersistMediaInfoEnabled;

            LibraryService = new LibraryService(libraryManager, providerManager, fileSystem);
            MediaInfoService = new MediaInfoService(libraryManager, fileSystem, itemRepository, jsonSerializer);

            this.libraryManager.ItemAdded += this.OnItemAdded;
            this.libraryManager.ItemRemoved += this.OnItemRemoved;
            this.logger.Info($"插件 {this.Name} 加载完成");
        }

        public override string Description => "Persist/restore MediaInfo to speed up first playback.";

        public override Guid Id => this.id;

        public sealed override string Name => PluginName;

        public PluginConfiguration Options => this.GetOptions();

        public ILogger Logger => this.logger;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = this.GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Resources.ThumbImage.png");
        }

        protected override void OnCreatePageInfo(PluginPageInfo pageInfo)
        {
            pageInfo.Name = "MediaInfoKeeper";
            pageInfo.DisplayName = "MediaInfoKeeper";
            pageInfo.MenuIcon = "video_settings";
            pageInfo.EnableInMainMenu = true;
            pageInfo.IsMainConfigPage = true;
        }

        protected override PluginConfiguration OnBeforeShowUI(PluginConfiguration options)
        {
            var list = new List<EditorSelectOption>();
            foreach (var folder in this.libraryManager.GetVirtualFolders())
            {
                if (folder == null)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(folder.Name) ? folder.ItemId : folder.Name;
                list.Add(new EditorSelectOption
                {
                    Value = folder.ItemId,
                    Name = name,
                    IsEnabled = true
                });
            }

            options.LibraryList = list;
            options.LibraryScope.LibraryList = list;
            options.GitHub.CurrentVersion = GetCurrentVersion();
            options.GitHub.LatestReleaseVersion = GetLatestReleaseVersion();
            return base.OnBeforeShowUI(options);
        }

        protected override bool OnOptionsSaving(PluginConfiguration options)
        {
            options.LibraryScope.CatchupLibraries = NormalizeScopedLibraries(options.LibraryScope.CatchupLibraries);
            options.LibraryScope.ScheduledTaskLibraries = NormalizeScopedLibraries(options.LibraryScope.ScheduledTaskLibraries);
            return base.OnOptionsSaving(options);
        }

        /// <summary>应用配置变更并更新缓存标记。</summary>
        protected override void OnOptionsSaved(PluginConfiguration options)
        {
            this.currentPersistMediaInfo = options.General.PersistMediaInfoEnabled;

            this.logger.Info($"{this.Name} 配置已更新。");
            this.logger.Info($"PersistMediaInfoEnabled 设置为 {options.General.PersistMediaInfoEnabled}");
            this.logger.Info($"MediaInfoJsonRootFolder 设置为 {(string.IsNullOrEmpty(options.General.MediaInfoJsonRootFolder) ? "EMPTY" : options.General.MediaInfoJsonRootFolder)}");
            this.logger.Info($"DeleteMediaInfoJsonOnRemove 设置为 {options.General.DeleteMediaInfoJsonOnRemove}");
            this.logger.Info($"CatchupLibraries 设置为 {(string.IsNullOrEmpty(options.LibraryScope.CatchupLibraries) ? "EMPTY" : options.LibraryScope.CatchupLibraries)}");
            this.logger.Info($"ScheduledTaskLibraries 设置为 {(string.IsNullOrEmpty(options.LibraryScope.ScheduledTaskLibraries) ? "EMPTY" : options.LibraryScope.ScheduledTaskLibraries)}");
            this.logger.Info($"EnableMetadataProvidersWatcher 设置为 {options.General.EnableMetadataProvidersWatcher}");

            FfprobeGuard.Configure(options.General.DisableSystemFfprobe);
            MetadataProvidersWatcher.Configure(options.General.EnableMetadataProvidersWatcher);
        }

        private string NormalizeScopedLibraries(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in this.libraryManager.GetVirtualFolders())
            {
                if (folder == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(folder.ItemId))
                {
                    lookup[folder.ItemId] = folder.ItemId;
                }

                if (!string.IsNullOrWhiteSpace(folder.Name))
                {
                    lookup[folder.Name.Trim()] = folder.ItemId;
                }
            }

            var tokens = raw.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var normalized = new List<string>();
            foreach (var token in tokens)
            {
                var value = token.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (lookup.TryGetValue(value, out var mapped))
                {
                    normalized.Add(mapped);
                }
                else
                {
                    normalized.Add(value);
                }
            }

            return string.Join(",", normalized);
        }

        /// <summary>处理新入库条目，按配置执行持久化或恢复。</summary>
        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {

            try
            {
                this.logger.Info($"{e.Item.Path} 新增剧集事件");
                if (!this.currentPersistMediaInfo)
                {
                    // 未启用持久化，直接跳过。
                    return;
                }

                if (!(e.Item is Video))
                {
                    // 仅处理视频条目。
                    return;
                }

                if (!LibraryService.IsItemInScope(e.Item))
                {
                    // 条目不在选定媒体库范围内。
                    this.logger.Info("跳过处理: 不在选定媒体库范围");
                    return;
                }

                var directoryService = new DirectoryService(this.logger, this.fileSystem);
                // 判断当前条目是否已有 MediaInfo。
                var hasMediaInfo = LibraryService.HasMediaInfo(e.Item);

                if (!hasMediaInfo)
                {
                    // 优先尝试从 JSON 恢复，减少首次提取耗时。
                    this.logger.Info("尝试从 JSON 恢复 MediaInfo");
                    var restoreResult = await MediaInfoService.DeserializeMediaInfo(e.Item, directoryService, "OnItemAdded", true).ConfigureAwait(false);
                    
                    // 如果不存在Json文件，则使用ffprobe 提取一次
                    if (restoreResult == MediaInfoService.MediaInfoRestoreResult.Failed)
                    {
                        // 恢复失败时先触发媒体信息提取，再写入 JSON。
                        this.logger.Info("恢复失败，开始提取 MediaInfo");

                        // 触发一次刷新以提取 MediaInfo。
                        e.Item.DateLastRefreshed = new DateTimeOffset();
                        using (FfprobeGuard.Allow())
                        {
                            this.logger.Info("恢复失败，是初次入库，进行下载元数据");
                            // 构建用于媒体信息提取的刷新参数与库选项。
                            var metadataRefreshOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                            {
                                EnableRemoteContentProbe = true,
                                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                                ReplaceAllMetadata = true,
                                ReplaceAllImages = false
                            };

                            var itemCollectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(e.Item);
                            var itemLibraryOptions = this.libraryManager.GetLibraryOptions(e.Item);
                            e.Item.DateLastRefreshed = new DateTimeOffset();
                            await this.providerManager
                                .RefreshSingleItem(e.Item, metadataRefreshOptions, itemCollectionFolders, itemLibraryOptions, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        // 提取完成后写入 JSON。
                        this.logger.Info("MediaInfo 提取完成，写入 JSON");
                        _ = MediaInfoService.SerializeMediaInfo(e.Item.InternalId, directoryService, true, "OnItemAdded WriteNewJson");
                    }
                    // 使用Json媒体信息数据，恢复成功后扫描所在物理路径，确保库状态刷新。
                    else if (restoreResult == MediaInfoService.MediaInfoRestoreResult.Restored)
                    {
                        var itemPath = e.Item.Path ?? e.Item.ContainingFolderPath ?? e.Item.Id.ToString();
                        this.logger.Info($"JSON 恢复成功，准备扫描物理路径 item: {itemPath}");
                        var scanOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                        {
                            EnableRemoteContentProbe = false,
                            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                            ReplaceAllMetadata = false,
                            ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                            ReplaceAllImages = false,
                            EnableThumbnailImageExtraction = false,
                            EnableSubtitleDownloading = false
                        };

                        var parentPath = e.Item.ContainingFolderPath;
                        if (!string.IsNullOrEmpty(parentPath))
                        {
                            if (!this.fileSystem.DirectoryExists(parentPath))
                            {
                                this.logger.Info($"物理路径不存在，跳过扫描: {parentPath}");
                            }
                            else
                            {
                                var parentFolder = this.libraryManager.FindByPath(parentPath, true) as Folder;
                                if (parentFolder != null)
                                {
                                    this.logger.Info($"刷新父级条目: {parentPath}");
                                    try
                                    {
                                        var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(parentFolder);
                                        var libraryOptions = this.libraryManager.GetLibraryOptions(parentFolder);
                                        using (FfprobeGuard.Allow())
                                        {
                                            await this.providerManager
                                            .RefreshSingleItem(parentFolder, scanOptions, collectionFolders, libraryOptions, CancellationToken.None)
                                            .ConfigureAwait(false);
                                        }
                                    }
                                    catch (Exception refreshEx)
                                    {
                                        this.logger.Error($"刷新父级条目失败: {parentPath}");
                                        this.logger.Error(refreshEx.Message);
                                        this.logger.Debug(refreshEx.StackTrace);
                                    }
                                }
                                else
                                {
                                    this.logger.Info($"未找到物理路径对应的文件夹项，跳过刷新: {parentPath}");
                                }
                            }
                        }
                        else
                        {
                            this.logger.Info($"未找到条目所在物理路径，跳过扫描 item: {itemPath}");
                        }
                    }
                }
                // 已有 MediaInfo 时，直接用媒体信息覆盖写入 JSON，保持最新。
                else
                {
                    this.logger.Info("已有 MediaInfo，覆盖写入 JSON");
                    _ = MediaInfoService.SerializeMediaInfo(e.Item.InternalId, directoryService, true, "OnItemAdded Overwrite");
                }

            }
            catch (Exception ex)
            {
                // 记录异常，避免影响库事件流程。
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
            }
        }



        /// <summary>条目移除且非恢复模式时，删除已持久化的 JSON。</summary>
        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            this.logger.Info($"{e.Item.Path} 删除剧集事件");
            // 未开启删除开关时直接跳过。
            if (!this.Options.General.DeleteMediaInfoJsonOnRemove || !this.Options.General.PersistMediaInfoEnabled)
            {
                return;
            }

            if (!(e.Item is Video))
            {
                return;
            }

            if (!LibraryService.IsItemInScope(e.Item))
            {
                return;
            }

            var directoryService = new DirectoryService(this.logger, this.fileSystem);
            logger.Info("同步删除 媒体信息 Json");
            MediaInfoService.DeleteMediaInfoJson(e.Item, directoryService, "Item Removed Event");
        }

        private string GetLatestReleaseVersion()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - latestVersionCheckedUtc < LatestVersionCacheDuration && !string.IsNullOrWhiteSpace(latestReleaseVersionCache))
            {
                return latestReleaseVersionCache;
            }

            lock (LatestVersionLock)
            {
                if (now - latestVersionCheckedUtc < LatestVersionCacheDuration && !string.IsNullOrWhiteSpace(latestReleaseVersionCache))
                {
                    return latestReleaseVersionCache;
                }

                latestVersionCheckedUtc = now;
                latestReleaseVersionCache = FetchLatestReleaseVersion();
                return latestReleaseVersionCache;
            }
        }

        private string FetchLatestReleaseVersion()
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, GitHubLatestReleaseUrl);
                request.Headers.UserAgent.ParseAdd("MediaInfoKeeper");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var response = HttpClient.SendAsync(request).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    this.logger.Info($"获取 GitHub 最新版本失败: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return "获取失败";
                }

                var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("tag_name", out var tagElement))
                {
                    var tagName = tagElement.GetString();
                    if (!string.IsNullOrWhiteSpace(tagName))
                    {
                        return tagName.Trim();
                    }
                }

                if (document.RootElement.TryGetProperty("name", out var nameElement))
                {
                    var releaseName = nameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(releaseName))
                    {
                        return releaseName.Trim();
                    }
                }

                return "未知";
            }
            catch (Exception ex)
            {
                this.logger.Info($"获取 GitHub 最新版本失败: {ex.Message}");
                this.logger.Debug(ex.StackTrace);
                return "获取失败";
            }
        }

        private string GetCurrentVersion()
        {
            var version = this.GetType().Assembly.GetName().Version;
            return version == null ? "未知" : version.ToString(3);
        }
    }
}


