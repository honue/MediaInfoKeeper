using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.View;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Services;
using MediaInfoKeeper.Services.IntroSkip;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaInfoKeeper.Web;

namespace MediaInfoKeeper
{
    /// <summary>
    /// The plugin.
    /// </summary>
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages
    {
        public const string PluginName = "MediaInfoKeeper";
        public const string TaskCategoryName = "Auto-MediaInfoKeeper";

        public static Plugin Instance { get; private set; }
        public static MediaInfoService MediaInfoService { get; private set; }
        public static LibraryService LibraryService { get; private set; }
        public static IntroSkipChapterApi IntroSkipChapterApi { get; private set; }
        public static IntroSkipPlaySessionMonitor IntroSkipPlaySessionMonitor { get; private set; }
        public static IntroScanService IntroScanService { get; private set; }

        private readonly Guid id = new Guid("874D7056-072D-43A4-16DD-BC32665B9563");
        private readonly ILogger logger;
        private List<IPluginUIPageController> pages;

        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;
        private readonly IUserManager userManager;
        private readonly IUserDataManager userDataManager;
        private readonly ISessionManager sessionManager;
        private readonly IMediaMountManager mediaMountManager;
        private readonly IApplicationHost applicationHost;

        internal static IProviderManager ProviderManager { get; private set; }
        internal static IFileSystem FileSystem { get; private set; }
        internal static ILibraryManager LibraryManager { get; private set; }
        internal IApplicationHost AppHost => this.applicationHost;

        private bool PlugginEnabled;
        internal readonly PluginOptionsStore OptionsStore;
        internal readonly MainPageOptionsStore MainPageOptionsStore;
        internal readonly GitHubOptionsStore GitHubOptionsStore;
        internal readonly IntroSkipOptionsStore IntroSkipOptionsStore;
        internal readonly ProxyOptionsStore ProxyOptionsStore;
        internal readonly EnhanceChineseSearchOptionsStore EnhanceChineseSearchOptionsStore;
        internal readonly MetaDataOptionsStore MetaDataOptionsStore;
        private static readonly HttpClient HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        private static string latestReleaseVersionCache;
        private static readonly object ReleaseHistoryLock = new object();
        private static DateTimeOffset releaseHistoryCheckedUtc = DateTimeOffset.MinValue;
        private static string releaseHistoryBodyCache;
        private static readonly TimeSpan LatestVersionCacheDuration = TimeSpan.FromMinutes(30);
        private const string GitHubReleaseHistoryUrl = "https://api.github.com/repos/honue/MediaInfoKeeper/releases?per_page=100&page=";

        /// <summary>初始化插件并注册库事件处理。</summary>
        public Plugin(
            IApplicationHost applicationHost,
            ILogManager logManager,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IItemRepository itemRepository,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ISessionManager sessionManager,
            IMediaMountManager mediaMountManager,
            IServerConfigurationManager serverConfigurationManager,
            IJsonSerializer jsonSerializer,
            IFileSystem fileSystem)
        {
            Instance = this;
            this.logger = logManager.GetLogger(this.Name);
            this.logger.Info($"插件 {this.Name} 正在加载");

            this.applicationHost = applicationHost;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.userManager = userManager;
            this.userDataManager = userDataManager;
            this.sessionManager = sessionManager;
            this.mediaMountManager = mediaMountManager;
            ProviderManager = providerManager;
            FileSystem = fileSystem;
            LibraryManager = libraryManager;

            OptionsStore = new PluginOptionsStore(applicationHost, this.logger, this.Name,
                PrepareOptionsForUi, HandleOptionsSaving, HandleOptionsSaved);
            MainPageOptionsStore = new MainPageOptionsStore(OptionsStore);
            GitHubOptionsStore = new GitHubOptionsStore(OptionsStore);
            IntroSkipOptionsStore = new IntroSkipOptionsStore(OptionsStore);
            ProxyOptionsStore = new ProxyOptionsStore(OptionsStore);
            EnhanceChineseSearchOptionsStore = new EnhanceChineseSearchOptionsStore(OptionsStore);
            MetaDataOptionsStore = new MetaDataOptionsStore(OptionsStore);

            PatchManager.Initialize(this.logger, this.Options);

            this.PlugginEnabled = this.Options.MainPage?.PlugginEnabled ?? true;

            LibraryService = new LibraryService(libraryManager, providerManager, fileSystem, userDataManager);
            MediaInfoService = new MediaInfoService(libraryManager, fileSystem, itemRepository, jsonSerializer, mediaMountManager);
            IntroSkipChapterApi = new IntroSkipChapterApi(libraryManager, itemRepository, this.logger);
            IntroScanService = new IntroScanService(logManager, libraryManager);
            IntroSkipPlaySessionMonitor = new IntroSkipPlaySessionMonitor(
                libraryManager, userManager, sessionManager, this.logger);
            ShortcutMenuLoader.Initialize(serverConfigurationManager);

            if (this.Options.IntroSkip?.EnableIntroSkip == true)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(this.Options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(this.Options.IntroSkip.UserScope);
            }

            this.libraryManager.ItemAdded += this.OnItemAdded;
            this.libraryManager.ItemRemoved += this.OnItemRemoved;
            this.userDataManager.UserDataSaved += this.OnUserDataSaved;
            this.logger.Info($"插件 {this.Name} 加载完成");
        }

        public override string Description => "Persist/restore MediaInfo to speed up first playback.";

        public override Guid Id => this.id;

        public sealed override string Name => PluginName;

        public PluginConfiguration Options
        {
            get
            {
                var options = this.OptionsStore.GetOptions();
                options.MainPage ??= new MainPageOptions();
                return options;
            }
        }

        public ILogger Logger => this.logger;

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            var type = this.GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Resources.ThumbImage.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (this.pages == null)
                {
                    this.pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(this.GetPluginInfo(), this.MainPageOptionsStore,
                            this.GitHubOptionsStore, this.IntroSkipOptionsStore, this.ProxyOptionsStore,
                            this.EnhanceChineseSearchOptionsStore, this.MetaDataOptionsStore)
                    };
                }

                return this.pages.AsReadOnly();
            }
        }

        internal void PrepareOptionsForUi(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.Proxy ??= new ProxyOptions();
            options.GitHub ??= new GitHubOptions();
            options.EnhanceChineseSearch ??= new EnhanceChineseSearchOptions();
            options.MetaData ??= new MetaDataOptions();
            options.EnhanceChineseSearch.Initialize();
            options.MetaData.Initialize();

            var list = LibraryService.BuildLibrarySelectOptions();
            options.MainPage.LibraryList = list;
            options.IntroSkip.LibraryList = list;
            options.GitHub.CurrentVersion = GetCurrentVersion();
            options.GitHub.LatestReleaseVersion = GetLatestReleaseVersion();
            options.GitHub.ReleaseHistoryBody = GetReleaseHistoryBody();
        }

        internal bool HandleOptionsSaving(PluginConfiguration options)
        {
            if (options?.MainPage == null)
            {
                return true;
            }

            options.MainPage.CatchupLibraries = NormalizeScopedLibraries(options.MainPage.CatchupLibraries);
            options.MainPage.ScheduledTaskLibraries = NormalizeScopedLibraries(options.MainPage.ScheduledTaskLibraries);
            if (options.IntroSkip != null)
            {
                options.IntroSkip.LibraryScope = NormalizeScopedLibraries(options.IntroSkip.LibraryScope);
                options.IntroSkip.MarkerEnabledLibraryScope = NormalizeScopedLibraries(options.IntroSkip.MarkerEnabledLibraryScope);
            }
            return true;
        }

        /// <summary>应用配置变更并更新缓存标记。</summary>
        internal void HandleOptionsSaved(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.Proxy ??= new ProxyOptions();
            options.EnhanceChineseSearch ??= new EnhanceChineseSearchOptions();
            options.MetaData ??= new MetaDataOptions();

            this.PlugginEnabled = options.MainPage.PlugginEnabled;

            this.logger.Info($"{this.Name} 配置已更新。");
            this.logger.Info("[Main]");
            this.logger.Info($"启用插件 设置为 {options.MainPage.PlugginEnabled}");
            this.logger.Info($"入库时提取媒体信息 设置为 {options.MainPage.ExtractMediaInfoOnItemAdded}");
            this.logger.Info($"收藏时提取媒体信息 设置为 {options.MainPage.ExtractMediaInfoOnFavorite}");
            this.logger.Info($"MediaInfo JSON 存储根目录 设置为 {(string.IsNullOrEmpty(options.MainPage.MediaInfoJsonRootFolder) ? "空" : options.MainPage.MediaInfoJsonRootFolder)}");
            this.logger.Info($"条目移除时删除 JSON 设置为 {options.MainPage.DeleteMediaInfoJsonOnRemove}");
            this.logger.Info($"追更媒体库 设置为 {(string.IsNullOrEmpty(options.MainPage.CatchupLibraries) ? "空" : options.MainPage.CatchupLibraries)}");
            this.logger.Info($"计划任务媒体库 设置为 {(string.IsNullOrEmpty(options.MainPage.ScheduledTaskLibraries) ? "空" : options.MainPage.ScheduledTaskLibraries)}");
            this.logger.Info($"扫描最多并发数 设置为 {options.MainPage.MaxConcurrentCount}");

            this.logger.Info("[IntroSkip]");
            this.logger.Info($"启用 Strm 片头检测解锁 设置为 {options.IntroSkip.UnlockIntroSkip}");
            this.logger.Info($"入库时扫描片头 设置为 {options.IntroSkip.ScanIntroOnItemAdded}");
            this.logger.Info($"收藏时扫描片头 设置为 {options.IntroSkip.ScanIntroOnFavorite}");
            this.logger.Info($"保护片头标记 设置为 {options.IntroSkip.ProtectIntroMarkers}");
            this.logger.Info($"启用播放行为打标 设置为 {options.IntroSkip.EnableIntroSkip}");
            this.logger.Info($"片头检测库范围 设置为 {(string.IsNullOrEmpty(options.IntroSkip.MarkerEnabledLibraryScope) ? "空" : options.IntroSkip.MarkerEnabledLibraryScope)}");
            this.logger.Info($"打标库范围 设置为 {(string.IsNullOrEmpty(options.IntroSkip.LibraryScope) ? "空" : options.IntroSkip.LibraryScope)}");
            this.logger.Info($"用户范围 设置为 {(string.IsNullOrEmpty(options.IntroSkip.UserScope) ? "空" : options.IntroSkip.UserScope)}");

            this.logger.Info("[Search]");
            this.logger.Info($"启用增强搜索 设置为 {options.EnhanceChineseSearch.EnhanceChineseSearch}");
            this.logger.Info($"搜索范围 设置为 {(string.IsNullOrEmpty(options.EnhanceChineseSearch.SearchScope) ? "空" : options.EnhanceChineseSearch.SearchScope)}");
            this.logger.Info($"排除原始标题 设置为 {options.EnhanceChineseSearch.ExcludeOriginalTitleFromSearch}");

            this.logger.Info("[MetaData]");
            this.logger.Info($"启用剧集元数据变动监听 设置为 {options.MetaData.EnableMetadataProvidersWatcher}");
            this.logger.Info($"启用 TMDB 中文回退 设置为 {options.MetaData.EnableAlternativeTitleFallback}");
            this.logger.Info($"启用 TVDB 中文回退 设置为 {options.MetaData.EnableTvdbFallback}");
            this.logger.Info($"TMDB 备选语言 设置为 {options.MetaData.FallbackLanguages}");
            this.logger.Info($"TVDB 备选语言 设置为 {options.MetaData.TvdbFallbackLanguages}");
            this.logger.Info($"屏蔽非备选语言简介 设置为 {options.MetaData.BlockNonFallbackLanguage}");
            this.logger.Info($"启用 TMDB 剧集组刮削 设置为 {options.MetaData.EnableMovieDbEpisodeGroup}");
            this.logger.Info($"优先原语言海报 设置为 {options.MetaData.EnableOriginalPoster}");
            this.logger.Info($"启用本地剧集组文件 设置为 {options.MetaData.EnableLocalEpisodeGroup}");

            this.logger.Info("[Proxy]");
            this.logger.Info($"启用代理 设置为 {options.Proxy.EnableProxyServer}");
            this.logger.Info($"代理服务器地址 设置为 {(string.IsNullOrEmpty(options.Proxy.ProxyServerUrl) ? "空" : options.Proxy.ProxyServerUrl)}");
            this.logger.Info($"忽略证书验证 设置为 {options.Proxy.IgnoreCertificateValidation}");
            this.logger.Info($"写入环境变量 设置为 {options.Proxy.WriteProxyEnvVars}");
            this.logger.Info($"启用压缩传输 设置为 {options.Proxy.EnableGzip}");
            this.logger.Info($"自定义 TMDB API 域名 设置为 {(string.IsNullOrEmpty(options.Proxy.AlternativeTmdbApiUrl) ? "空" : options.Proxy.AlternativeTmdbApiUrl)}");
            this.logger.Info($"自定义 TMDB 图像域名 设置为 {(string.IsNullOrEmpty(options.Proxy.AlternativeTmdbImageUrl) ? "空" : options.Proxy.AlternativeTmdbImageUrl)}");
            this.logger.Info($"自定义 TMDB API 密钥 设置为 {(string.IsNullOrEmpty(options.Proxy.AlternativeTmdbApiKey) ? "空" : "***")}");

            PatchManager.Configure(options);

            if (options.IntroSkip.EnableIntroSkip)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(options.IntroSkip.UserScope);
            }
            else
            {
                IntroSkipPlaySessionMonitor.Dispose();
            }

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
                if (!this.PlugginEnabled)
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
                    var restoreResult = await MediaInfoService.DeserializeMediaInfo(e.Item, directoryService, "OnItemAdded").ConfigureAwait(false);

                    // 如果不存在Json文件，则使用ffprobe 提取一次
                    if (restoreResult == MediaInfoService.MediaInfoRestoreResult.Failed)
                    {
                        if (!this.Options.MainPage.ExtractMediaInfoOnItemAdded)
                        {
                            this.logger.Info("已关闭入库时提取媒体信息，跳过");
                            return;
                        }

                        // 恢复失败时先触发媒体信息提取，再写入 JSON。
                        this.logger.Info("恢复失败，初次入库，开始提取媒体信息");

                        // 触发一次刷新以提取 MediaInfo。
                        using (FfprobeGuard.Allow())
                        {
                            // 构建用于媒体信息提取的刷新参数与库选项。
                            var metadataRefreshOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                            {
                                EnableRemoteContentProbe = true,
                                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                                ReplaceAllMetadata = true,
                                ReplaceAllImages = false
                            };

                            var itemCollectionFolders = this.libraryManager.GetCollectionFolders(e.Item).Cast<BaseItem>().ToArray();
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
                                        var collectionFolders = this.libraryManager.GetCollectionFolders(parentFolder).Cast<BaseItem>().ToArray();
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

                if (this.Options.IntroSkip?.ScanIntroOnItemAdded == true && e.Item is Episode episode)
                {
                    IntroScanService.QueueEpisodeScan(episode, "OnItemAdded");
                }

            }
            catch (Exception ex)
            {
                // 记录异常，避免影响库事件流程。
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
            }
        }

        /// <summary> 收藏喜爱事件处 </summary>
        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                if (!this.PlugginEnabled)
                {
                    return;
                }

                var item = e.Item;
                var userData = e.UserData;
                if (item == null || userData == null)
                {
                    return;
                }

                if (item.ExtraType != null)
                {
                    return;
                }

                if (!userData.IsFavorite)
                {
                    return;
                }

                var userName = e.User?.Name ?? "unknown";
                logger.Info($"收藏事件: 用户={userName}, 条目={(item.FileName ?? item.Path ?? item.Id.ToString())}");

                var canExtract = this.Options.MainPage?.ExtractMediaInfoOnFavorite == true &&
                                 (item is Episode || item is Season || item is Series);
                var canScanIntro = this.Options.IntroSkip?.ScanIntroOnFavorite == true &&
                                (item is Episode || item is Season || item is Series);

                if (!canExtract && !canScanIntro)
                {
                    return;
                }
                
                if (canScanIntro)
                {
                    var episodes = LibraryService.GetSeriesEpisodesFromItem(item);
                    if (episodes.Count > 0)
                    {
                        foreach (var seriesEpisode in episodes)
                        {
                            IntroScanService.QueueEpisodeScan(seriesEpisode, "OnFavorite");
                        }
                    }
                    else
                    {
                        this.logger.Info("OnFavorite 片头扫描跳过: 未找到系列条目");
                    }
                }

                else if (canExtract)
                {
                    _ = Task.Run(async () =>
                    {
                        var seriesEpisodes = LibraryService.GetSeriesEpisodesFromItem(item);
                        if (seriesEpisodes.Count > 0)
                        {
                            await MediaInfoTaskRunner
                                .ProcessItemsAsync(
                                    seriesEpisodes.Cast<BaseItem>().ToList(),
                                    async target =>
                                    {
                                        if (target == null)
                                        {
                                            return;
                                        }

                                        var displayName = target.Path ?? target.Name;
                                        var directoryService = new DirectoryService(this.logger, this.fileSystem);
                                        var workItem = this.libraryManager.GetItemById(target.InternalId) ?? target;

                                        try
                                        {
                                            if (LibraryService.HasMediaInfo(workItem))
                                            {
                                                this.logger.Info($"OnFavorite 已存在 MediaInfo，跳过处理: {displayName}");
                                                return;
                                            }

                                            var restoreResult = await MediaInfoService
                                                .DeserializeMediaInfo(workItem, directoryService, "OnFavorite Restore")
                                                .ConfigureAwait(false);
                                            if (restoreResult == MediaInfoService.MediaInfoRestoreResult.Restored ||
                                                restoreResult == MediaInfoService.MediaInfoRestoreResult.AlreadyExists)
                                            {
                                                this.logger.Info($"OnFavorite JSON 恢复成功，跳过 ffprobe: {displayName}");
                                                return;
                                            }

                                            var metadataRefreshOptions = MediaInfoService.GetMediaInfoRefreshOptions();
                                            var collectionFolders = this.libraryManager.GetCollectionFolders(workItem).Cast<BaseItem>().ToArray();
                                            var libraryOptions = this.libraryManager.GetLibraryOptions(workItem);
                                            using (FfprobeGuard.Allow())
                                            {
                                                workItem.DateLastRefreshed = new DateTimeOffset();
                                                await this.providerManager
                                                    .RefreshSingleItem(workItem, metadataRefreshOptions, collectionFolders, libraryOptions, CancellationToken.None)
                                                    .ConfigureAwait(false);
                                            }

                                            _ = MediaInfoService.SerializeMediaInfo(workItem.InternalId, directoryService, true, "OnFavorite Extract");
                                            this.logger.Info($"OnFavorite 媒体信息提取完成: {displayName}");
                                        }
                                        catch (Exception ex)
                                        {
                                            this.logger.Error($"OnFavorite 媒体信息提取失败: {displayName}");
                                            this.logger.Error(ex.Message);
                                            this.logger.Debug(ex.StackTrace);
                                        }
                                    },
                                    this.logger,
                                    CancellationToken.None,
                                    null)
                                .ConfigureAwait(false);
                        }
                    });
                }


            }
            catch (Exception ex)
            {
                this.logger.Error("收藏事件处理异常");
                this.logger.Error(ex.Message);
                this.logger.Debug(ex.StackTrace);
            }
        }

        /// <summary>条目移除且非恢复模式时，删除已持久化的 JSON。</summary>
        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            this.logger.Info($"{e.Item.Path} 删除剧集事件");
            // 未开启删除开关时直接跳过。
            if (!this.Options.MainPage.DeleteMediaInfoJsonOnRemove || !this.Options.MainPage.PlugginEnabled)
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
            EnsureReleaseHistoryCache();
            return latestReleaseVersionCache;
        }

        private string GetCurrentVersion()
        {
            var version = this.GetType().Assembly.GetName().Version;
            return version == null ? "未知" : $"v{version.ToString(4)}";
        }

        private string GetReleaseHistoryBody()
        {
            EnsureReleaseHistoryCache();
            return releaseHistoryBodyCache;
        }

        private void EnsureReleaseHistoryCache()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - releaseHistoryCheckedUtc < LatestVersionCacheDuration &&
                !string.IsNullOrWhiteSpace(releaseHistoryBodyCache))
            {
                return;
            }

            lock (ReleaseHistoryLock)
            {
                if (now - releaseHistoryCheckedUtc < LatestVersionCacheDuration &&
                    !string.IsNullOrWhiteSpace(releaseHistoryBodyCache))
                {
                    return;
                }

                releaseHistoryCheckedUtc = now;
                var historyInfo = FetchReleaseHistoryInfo();
                releaseHistoryBodyCache = historyInfo.HistoryBody;
                latestReleaseVersionCache = historyInfo.LatestVersion;
            }
        }

        private ReleaseHistoryInfo FetchReleaseHistoryInfo()
        {
            try
            {
                var sb = new StringBuilder();
                var page = 1;
                var latestAssigned = false;
                var latestVersion = "未知";
                while (true)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{GitHubReleaseHistoryUrl}{page}");
                    request.Headers.UserAgent.ParseAdd("MediaInfoKeeper");
                    request.Headers.Accept.ParseAdd("application/vnd.github+json");

                    var token = this.Options?.GitHub?.GitHubToken;
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
                    }

                    using var response = HttpClient.SendAsync(request).GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        this.logger.Info($"获取 GitHub 历史版本失败: {(int)response.StatusCode} {response.ReasonPhrase}");
                        return new ReleaseHistoryInfo("获取失败", "获取失败");
                    }

                    var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Array ||
                        document.RootElement.GetArrayLength() == 0)
                    {
                        break;
                    }

                    foreach (var release in document.RootElement.EnumerateArray())
                    {
                        var tag = release.TryGetProperty("tag_name", out var tagElement)
                            ? tagElement.GetString()
                            : string.Empty;
                        var name = release.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString()
                            : string.Empty;
                        var body = release.TryGetProperty("body", out var bodyElement)
                            ? bodyElement.GetString()
                            : string.Empty;
                        var publishedAt = release.TryGetProperty("published_at", out var publishedElement)
                            ? publishedElement.GetString()
                            : string.Empty;
                        var publishedAtLocal = publishedAt;
                        if (!string.IsNullOrWhiteSpace(publishedAt) &&
                            DateTimeOffset.TryParse(publishedAt, out var publishedOffset))
                        {
                            publishedAtLocal = publishedOffset
                                .ToOffset(TimeSpan.FromHours(8))
                                .ToString("yyyy-MM-dd HH:mm:ss");
                        }

                        if (string.IsNullOrWhiteSpace(body))
                        {
                            body = "无更新说明";
                        }

                        if (!latestAssigned)
                        {
                            latestVersion = !string.IsNullOrWhiteSpace(tag) ? tag.Trim()
                                : !string.IsNullOrWhiteSpace(name) ? name.Trim()
                                : "未知";
                            latestAssigned = true;
                        }

                        sb.Append(string.IsNullOrWhiteSpace(tag) ? "Release" : tag.Trim());
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            sb.Append(" - ").Append(name.Trim());
                        }

                        if (!string.IsNullOrWhiteSpace(publishedAtLocal))
                        {
                            sb.Append(" (").Append(publishedAtLocal.Trim()).Append(')');
                        }

                        sb.AppendLine();
                        sb.AppendLine(body.Trim());
                        sb.AppendLine("----");
                    }

                    page++;
                }

                if (sb.Length == 0)
                {
                    return new ReleaseHistoryInfo("暂无发布记录", latestVersion);
                }

                return new ReleaseHistoryInfo(sb.ToString().TrimEnd(), latestVersion);
            }
            catch (Exception ex)
            {
                this.logger.Info($"获取 GitHub 历史版本失败: {ex.Message}");
                this.logger.Debug(ex.StackTrace);
                return new ReleaseHistoryInfo("获取失败", "获取失败");
            }
        }

        private readonly struct ReleaseHistoryInfo
        {
            public ReleaseHistoryInfo(string historyBody, string latestVersion)
            {
                HistoryBody = historyBody;
                LatestVersion = latestVersion;
            }

            public string HistoryBody { get; }

            public string LatestVersion { get; }
        }
    }
}
