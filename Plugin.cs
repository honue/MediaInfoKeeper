using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using Emby.Web.GenericEdit.Elements;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.View;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Services;
using MediaInfoKeeper.Services.IntroSkip;
using MediaInfoKeeper.Store;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Web;
using MediaInfoKeeper.ScheduledTask;
using static MediaInfoKeeper.Options.EnhanceOptions;

namespace MediaInfoKeeper
{
    /// <summary>
    /// The plugin.
    /// </summary>
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages, IDisposable
    {
        public const string PluginName = "MediaInfoKeeper";
        public const string TaskCategoryName = "Auto-MediaInfoKeeper";

        public static Plugin Instance { get; private set; }
        public static MediaInfoService MediaInfoService { get; private set; }
        
        public static ChaptersStore ChaptersStore { get; private set; }
        public static MediaSourceInfoStore MediaSourceInfoStore { get; private set; }
        public static EmbeddedInfoStore EmbeddedInfoStore { get; private set; }
        public static LibraryService LibraryService { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static IntroSkipChapterApi IntroSkipChapterApi { get; private set; }
        public static IntroSkipPlaySessionMonitor IntroSkipPlaySessionMonitor { get; private set; }
        public static IntroScanService IntroScanService { get; private set; }
        public static PrefetchService PrefetchService { get; private set; }
        public static StrmFileWatcher StrmFileWatcher { get; private set; }
        public static ExternalFiles ExternalFiles { get; private set; }
        public static DanmuService DanmuService { get; private set; }
        internal static ReleaseInfoService ReleaseInfoService { get; private set; }

        private readonly Guid id = new Guid("874D7056-072D-43A4-16DD-BC32665B9563");
        private readonly ILogger logger;
        private List<IPluginUIPageController> pages;

        private readonly ILibraryManager libraryManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly IProviderManager providerManager;
        private readonly IItemRepository itemRepository;
        private readonly IFileSystem fileSystem;
        private readonly IUserManager userManager;
        private readonly IUserDataManager userDataManager;
        private readonly ISessionManager sessionManager;
        private readonly IMediaMountManager mediaMountManager;
        private readonly IApplicationHost applicationHost;
        private readonly ITaskManager taskManager;
        private readonly ReleaseInfoService releaseInfoService;

        internal static IProviderManager ProviderManager { get; private set; }
        internal static IFileSystem FileSystem { get; private set; }
        internal static ILibraryManager LibraryManager { get; private set; }
        internal static IHttpClient SharedHttpClient { get; private set; }
        internal static ILogger SharedLogger { get; private set; }
        internal IApplicationHost AppHost => this.applicationHost;
        internal IItemRepository ItemRepository => this.itemRepository;

        private bool PlugginEnabled;
        internal readonly PluginOptionsStore OptionsStore;
        internal readonly MainPageOptionsStore MainPageOptionsStore;
        internal readonly MediaInfoOptionsStore MediaInfoOptionsStore;
        internal readonly GitHubOptionsStore GitHubOptionsStore;
        internal readonly IntroSkipOptionsStore IntroSkipOptionsStore;
        internal readonly NetWorkOptionsStore NetWorkOptionsStore;
        internal readonly EnhanceOptionsStore EnhanceOptionsStore;
        internal readonly MetaDataOptionsStore MetaDataOptionsStore;
#if DEBUG
        internal readonly DebugOptionsStore DebugOptionsStore;
#endif
        /// <summary>初始化插件并注册库事件处理。</summary>
        public Plugin(
            IApplicationHost applicationHost,
            ILogManager logManager,
            ILibraryManager libraryManager,
            ILibraryMonitor libraryMonitor,
            IProviderManager providerManager,
            IItemRepository itemRepository,
            IUserManager userManager,
            IUserDataManager userDataManager,
            ISessionManager sessionManager,
            INotificationManager notificationManager,
            IActivityManager activityManager,
            IMediaMountManager mediaMountManager,
            IMediaSourceManager mediaSourceManager,
            IMediaProbeManager mediaProbeManager,
            IHttpClient httpClient,
            IServerConfigurationManager serverConfigurationManager,
            ILocalizationManager localizationManager,
            IJsonSerializer jsonSerializer,
            IFileSystem fileSystem)
        {
            Instance = this;
            this.logger = logManager.GetLogger(this.Name);
            this.logger.Info($"插件 {this.Name} 正在加载");

            this.applicationHost = applicationHost;
            this.libraryManager = libraryManager;
            this.libraryMonitor = libraryMonitor;
            this.providerManager = providerManager;
            this.itemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.userManager = userManager;
            this.userDataManager = userDataManager;
            this.sessionManager = sessionManager;
            this.mediaMountManager = mediaMountManager;
            this.taskManager = applicationHost.Resolve<ITaskManager>();
            this.releaseInfoService = new ReleaseInfoService(httpClient, this.logger, () => this.Options);
            ReleaseInfoService = this.releaseInfoService;
            ProviderManager = providerManager;
            FileSystem = fileSystem;
            LibraryManager = libraryManager;
            SharedHttpClient = httpClient;
            SharedLogger = this.logger;

            OptionsStore = new PluginOptionsStore(applicationHost, this.logger, this.Name,
                PrepareOptionsForUi, HandleOptionsSaving, HandleOptionsSaved);
            MainPageOptionsStore = new MainPageOptionsStore(OptionsStore);
            MediaInfoOptionsStore = new MediaInfoOptionsStore(OptionsStore);
            GitHubOptionsStore = new GitHubOptionsStore(OptionsStore);
            IntroSkipOptionsStore = new IntroSkipOptionsStore(OptionsStore);
            NetWorkOptionsStore = new NetWorkOptionsStore(OptionsStore);
            EnhanceOptionsStore = new EnhanceOptionsStore(OptionsStore);
            MetaDataOptionsStore = new MetaDataOptionsStore(OptionsStore);
#if DEBUG
            DebugOptionsStore = new DebugOptionsStore(OptionsStore);
#endif

            ExternalFiles = new ExternalFiles(
                libraryManager,
                fileSystem,
                mediaProbeManager,
                localizationManager,
                itemRepository);

            var initialOptions = this.Options;
            PersistOptionsOnStartup(initialOptions);
            PatchManager.Initialize(this.logger, initialOptions, activityManager);

            this.PlugginEnabled = initialOptions.MainPage?.PlugginEnabled ?? true;
            LogOptionsSnapshot(initialOptions, "已加载");

            LibraryService = new LibraryService(libraryManager, providerManager, fileSystem, userManager, userDataManager, mediaMountManager);
            MediaInfoService = new MediaInfoService(libraryManager, mediaSourceManager, fileSystem);
            ChaptersStore = new ChaptersStore(itemRepository, fileSystem, jsonSerializer);
            MediaSourceInfoStore = new MediaSourceInfoStore(libraryManager, itemRepository, fileSystem, jsonSerializer);
            EmbeddedInfoStore = new EmbeddedInfoStore(jsonSerializer);
            DanmuService = new DanmuService(logManager, httpClient);

            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager);
            IntroSkipChapterApi = new IntroSkipChapterApi(libraryManager, itemRepository, this.logger);
            IntroScanService = new IntroScanService(logManager, libraryManager, fileSystem);
            IntroSkipPlaySessionMonitor = new IntroSkipPlaySessionMonitor(
                libraryManager, userManager, sessionManager, this.logger);
            PrefetchService = new PrefetchService(
                libraryManager, sessionManager, this.logger);
            StrmFileWatcher = new StrmFileWatcher(libraryMonitor, LibraryService, this.logger);
            PluginWebResourceLoader.Initialize(serverConfigurationManager);
            PrefetchService.Initialize();
            this.releaseInfoService.Start();

            if (this.Options.IntroSkip?.EnableIntroMarker == true || this.Options.IntroSkip?.EnableCreditsMarker == true)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(this.Options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(this.Options.IntroSkip.UserScope);
            }

            ConfigureStrmFileWatcher();

            this.libraryManager.ItemAdded += this.OnItemAdded;
            this.libraryManager.ItemRemoved += this.OnItemRemoved;
            this.userDataManager.UserDataSaved += this.OnUserDataSaved;
            this.providerManager.RefreshCompleted += this.OnRefreshCompleted;
            CollectionFolder.LibraryOptionsUpdated += this.OnLibraryOptionsUpdated;

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
                options.MainPage.PrepareScheduledTaskEditorForUi();
                options.GetMediaInfoOptions();
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
                        new MainPageController(this.applicationHost, this.GetPluginInfo(), this.MainPageOptionsStore,
                            this.MediaInfoOptionsStore,
                            this.IntroSkipOptionsStore, this.NetWorkOptionsStore,
                            this.EnhanceOptionsStore, this.MetaDataOptionsStore
#if DEBUG
                            , this.DebugOptionsStore
#endif
                            )
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
            options.GetMediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.GetNetWorkOptions();
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
            {
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();
            }
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif
            options.IntroSkip.Initialize();
            options.GetMediaInfoOptions().Initialize();
            effectiveUpdatePluginOptions.Initialize();
            options.Enhance.Initialize();
            options.MetaData.Initialize();

            var list = LibraryService.BuildLibrarySelectOptions();
            options.MainPage.LibraryList = list;
            options.IntroSkip.LibraryList = list;
            options.MainPage.UpdatePluginVersionStatus = BuildVersionStatusItem();
            options.MainPage.UpdatePluginReleaseHistoryBody = string.IsNullOrWhiteSpace(this.releaseInfoService.HistoryBody)
                ? "加载中"
                : this.releaseInfoService.HistoryBody;
            options.MainPage.PrepareScheduledTaskEditorForUi();
        }

        internal bool HandleOptionsSaving(PluginConfiguration options)
        {
            if (options?.MainPage == null)
            {
                return true;
            }

            FinalizeOptionsForPersistence(options);
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
            {
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();
            }
            var netWorkOptions = options.GetNetWorkOptions();
            if (!LocalDiscoveryAddress.TryValidateConfiguredValue(
                    netWorkOptions.CustomLocalDiscoveryAddress,
                    out var normalizedDiscoveryAddress,
                    out var validationError))
            {
                this.logger.Warn("自定义本地发现地址校验失败：{0}", validationError);
                return false;
            }

            netWorkOptions.CustomLocalDiscoveryAddress = normalizedDiscoveryAddress;
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
            options.GetMediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
            {
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();
            }
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif
            var netWorkOptions = options.GetNetWorkOptions();

            this.PlugginEnabled = options.MainPage.PlugginEnabled;

            LogOptionsSnapshot(options, "已更新");
            
            PatchManager.Configure(options);

            if (options.IntroSkip.EnableIntroMarker || options.IntroSkip.EnableCreditsMarker)
            {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(options.IntroSkip.UserScope);
            }
            else
            {
                IntroSkipPlaySessionMonitor.Dispose();
            }

            ConfigureStrmFileWatcher();

        }

        internal void UpdatePinyinSortNameLastProcessedAt(DateTimeOffset processedAt)
        {
            var options = this.OptionsStore.GetOptions();
            options.Enhance ??= new EnhanceOptions();

            var current = options.Enhance.PinyinSortNameLastProcessedAt;
            if (current.HasValue && current.Value >= processedAt)
            {
                return;
            }

            options.Enhance.PinyinSortNameLastProcessedAt = processedAt;
            this.OptionsStore.SetOptionsSilently(options);
        }

        private void ConfigureStrmFileWatcher()
        {
            var safeOptions = this.OptionsStore.GetOptions() ?? new PluginConfiguration();
            safeOptions.MainPage ??= new MainPageOptions();
            safeOptions.GetMediaInfoOptions();

            StrmFileWatcher?.Configure(this.PlugginEnabled, safeOptions.MainPage.FileChangeRefreshDelaySeconds);
        }

        private void PersistOptionsOnStartup(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            FinalizeOptionsForPersistence(options);
            this.OptionsStore.SetOptionsSilently(options);
        }

        private void FinalizeOptionsForPersistence(PluginConfiguration options)
        {
            if (options == null)
            {
                return;
            }

            options.MainPage ??= new MainPageOptions();
            options.MainPage.ScheduledTasksEditor ??= new MainPageOptions.ScheduledTaskEditorOptions();
            options.GetMediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.GetNetWorkOptions();
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif

            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            effectiveUpdatePluginOptions.Initialize();
            options.MainPage.ScheduledTasksEditor.UpdatePlugin = effectiveUpdatePluginOptions;

            options.MainPage.CatchupLibraries = NormalizeScopedLibraries(options.MainPage.CatchupLibraries);
            var scheduledTasksEditor = options.MainPage.ScheduledTasksEditor;
            scheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries);
            scheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLibraries);
            scheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLibraries);
            scheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlLibraries);
            scheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries);
            scheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries);
            scheduledTasksEditor.ScanExternalFiles.ScanExternalFilesLibraries =
                NormalizeScopedLibraries(scheduledTasksEditor.ScanExternalFiles.ScanExternalFilesLibraries);

            if (options.IntroSkip != null)
            {
                options.IntroSkip.LibraryScope = NormalizeScopedLibraries(options.IntroSkip.LibraryScope);
            }
        }
        private void LogOptionsSnapshot(PluginConfiguration options, string action)
        {
            var optionsFilePath = this.OptionsStore.OptionsFilePath;
            if (!File.Exists(optionsFilePath))
            {
                this.logger.Debug("{0} 配置{1}: 配置文件不存在 {2}", this.Name, action, optionsFilePath);
                return;
            }

            try
            {
                var json = File.ReadAllText(optionsFilePath);
                var node = JsonNode.Parse(json);
                RedactSecret(node, nameof(MainPageOptions.UpdatePluginTaskEditorOptions.GitHubToken));
                RedactSecret(node, nameof(NetWorkOptions.AlternativeTmdbApiKey));
                this.logger.Debug("{0} 配置{1}: {2}", this.Name, action, node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? string.Empty);
            }
            catch (Exception ex)
            {
                this.logger.Debug("{0} 配置{1}: 配置 JSON 读取或解析失败 {2}", this.Name, action, ex.Message);
            }
        }

        private static void RedactSecret(JsonNode node, string propertyName)
        {
            if (node is JsonObject obj)
            {
                if (obj.ContainsKey(propertyName))
                {
                    obj[propertyName] = "***";
                }

                foreach (var child in obj.Select(pair => pair.Value).ToList())
                {
                    RedactSecret(child, propertyName);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    RedactSecret(child, propertyName);
                }
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
                if (!this.PlugginEnabled)
                {
                    // 未启用持久化，直接跳过。
                    return;
                }

                if (!(e.Item is Video) && !(e.Item is Audio))
                {
                    // 仅处理音视频条目。
                    return;
                }
                this.logger.Info($"新入库事件 {e.Item.FileName ?? e.Item.Path}");

                if (!LibraryService.IsItemInCatchupLibraryScope(e.Item))
                {
                    // 条目不在选定媒体库范围内。
                    this.logger.Info("跳过处理: 不在选定媒体库范围");
                    return;
                }

                // 判断当前条目是否已有 MediaInfo。
                var hasMediaInfo = MediaInfoService.HasMediaInfo(e.Item);
                if (!hasMediaInfo)
                {
                    // 优先尝试从 JSON 恢复，减少首次提取耗时。
                    this.logger.Debug("尝试从 JSON 恢复 MediaInfo");
                    var restoreResult = MediaSourceInfoStore.ApplyToItem(e.Item);
                    var shouldRefreshAfterRestore = restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Failed;
                    if (e.Item is Video)
                    {
                        ChaptersStore.ApplyToItem(e.Item);
                    }
                    else if (e.Item is Audio)
                    {
                        EmbeddedInfoStore.ApplyToItem(e.Item);
                    }

                    // 如果不存在Json文件，则使用ffprobe 提取一次
                    if (shouldRefreshAfterRestore)
                    {
                        if (!this.Options.GetMediaInfoOptions().ExtractMediaInfoOnItemAdded)
                        {
                            this.logger.Info($"已关闭入库提取媒体信息，跳过提取 item={e.Item.FileName ?? e.Item.Path}");
                        }
                        else
                        {
                            // 恢复失败时先触发媒体信息提取，再写入 JSON。
                            this.logger.Info($"入库媒体信息: 媒体信息缺失，开始提取 item={e.Item.FileName ?? e.Item.Path}");

                            // 触发一次刷新以提取 MediaInfo。
                            using (FfProcessGuard.Allow())
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
                                await RefreshTaskRunner.RunAsync(
                                        () => this.providerManager
                                            .RefreshSingleItem(e.Item, metadataRefreshOptions, itemCollectionFolders, itemLibraryOptions, CancellationToken.None))
                                    .ConfigureAwait(false);
                            }
                            this.logger.Info($"入库媒体信息: 提取完成并写入 JSON item={e.Item.FileName ?? e.Item.Path}");
                        }
                    }
                    // 使用Json媒体信息数据，恢复成功后扫描所在物理路径，确保库状态刷新。
                    else if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored)
                    {
                        var itemPath = e.Item.Path ?? e.Item.ContainingFolderPath ?? e.Item.Id.ToString();
                        var parentPath = e.Item.ContainingFolderPath;
                        this.logger.Info($"入库媒体信息: JSON 恢复成功 item={itemPath}");

                        if (string.IsNullOrEmpty(parentPath))
                        {
                            this.logger.Info($"未找到条目所在物理路径，跳过扫描 item: {itemPath}");
                        }
                        else if (!this.fileSystem.DirectoryExists(parentPath))
                        {
                            this.logger.Info($"物理路径不存在，跳过扫描: {parentPath}");
                        }
                        else
                        {
                            var parentFolder = this.libraryManager.FindByPath(parentPath, true) as Folder;
                            if (parentFolder == null)
                            {
                                this.logger.Info($"未找到物理路径对应的文件夹项，跳过刷新: {parentPath}");
                            }
                            else
                            {
                                // 仅触发目录校验/发现，不做元数据覆盖与远端抓取。
                                var discoverOnlyOptions = new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
                                {
                                    EnableRemoteContentProbe = false,
                                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                                    ReplaceAllMetadata = false,
                                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                                    ReplaceAllImages = false,
                                    EnableThumbnailImageExtraction = false,
                                    EnableSubtitleDownloading = false
                                };

                                this.logger.Info($"刷新父级条目: {parentPath}");
                                try
                                {
                                    var collectionFolders = this.libraryManager.GetCollectionFolders(parentFolder).Cast<BaseItem>().ToArray();
                                    var libraryOptions = this.libraryManager.GetLibraryOptions(parentFolder);
                                    await RefreshTaskRunner.RunAsync(
                                            () => this.providerManager
                                                .RefreshSingleItem(parentFolder, discoverOnlyOptions, collectionFolders, libraryOptions, CancellationToken.None))
                                        .ConfigureAwait(false);
                                }
                                catch (Exception refreshEx)
                                {
                                    this.logger.Error($"刷新父级条目失败: {parentPath}");
                                    this.logger.Error(refreshEx.Message);
                                    this.logger.Debug(refreshEx.StackTrace);
                                }
                            }
                        }
                    }
                }
                // 已有 MediaInfo 时，直接用媒体信息覆盖写入 JSON，保持最新。
                else
                {
                    this.logger.Debug("已有 MediaInfo，覆盖写入 JSON");
                    MediaInfoPersist.OverWritePersistedMedia(e.Item);
                }
                
                // 入库加入扫描片头队列
                if (this.Options.IntroSkip?.ScanIntroOnItemAdded == true && e.Item is Episode episode)
                {
                    IntroScanService.QueueEpisodeScan(episode, "OnItemAdded");
                }

                // 收藏入库通知分支
                if (e.Item is Episode newEpisode && newEpisode.ExtraType == null)
                {
                    var series = LibraryService.GetSeries(newEpisode.SeriesId);
                    if (series == null)
                    {
                        this.logger.Info($"收藏入库通知跳过: 未找到所属剧集，episodeId={newEpisode.InternalId}");
                    }
                    else
                    {
                        var users = LibraryService.GetFavoriteUsersBySeriesId(series.InternalId);
                        if (users.Count != 0)
                        {
                            this.logger.Info($"收藏入库事件: 剧集={series.Name} {newEpisode.Name}, 收藏用户={string.Join(", ", users)}");
                            var sentCount = NotificationApi.LibraryNewSendNotification(series, newEpisode, users);
                            if (sentCount > 0)
                            {
                                this.logger.Info($"已发送入库通知: 剧集={series.Name} {newEpisode.Name}, 通知用户数={sentCount}");
                            }
                            // 开始执行扫描收藏媒体信息和片头,避免重复，判断未开启所有入库扫描
                            var canScanIntro = this.Options.IntroSkip?.ScanIntroOnFavorite == true && this.Options.IntroSkip?.ScanIntroOnItemAdded == false;
                            if (canScanIntro)
                            {
                                IntroScanService.QueueEpisodeScan(newEpisode, "收藏入库");
                            }
                        }
                        else
                        {
                            this.logger.Debug($"收藏入库通知跳过: 剧集={series.Name}，无收藏用户");
                        }
                    }
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

                var canScanIntro = this.Options.IntroSkip?.ScanIntroOnFavorite == true &&
                                (item is Episode || item is Season || item is Series);

                if (!canScanIntro)
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
            this.logger.Info($"{e.Item.Path} 删除媒体事件");
            // 未开启删除开关时直接跳过。
            if (!this.Options.GetMediaInfoOptions().DeleteMediaInfoJsonOnRemove || !this.Options.MainPage.PlugginEnabled)
            {
                return;
            }

            if (!(e.Item is Video) && !(e.Item is Audio))
            {
                return;
            }

            if (!LibraryService.IsItemInCatchupLibraryScope(e.Item))
            {
                return;
            }

            logger.Info("同步删除 媒体信息 Json");
            MediaInfoDocument.DeleteMediaInfoJson(e.Item, new DirectoryService(this.logger, this.fileSystem), "Item Removed Event");
        }

        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            if (this.libraryManager.IsScanRunning)
            {
                return;
            }

            var options = this.Options;
            var item = e.Argument.Item;
            if (!(options.Enhance?.MergeMultiVersion == true && item.IsTopParent))
            {
                return;
            }

            if (MergeMultiVersionTask.IsInternalRefresh(item.InternalId))
            {
                return;
            }

            var library = e.Argument.CollectionFolders.OfType<CollectionFolder>().FirstOrDefault();

            if (library == null)
            {
                return;
            }

            var mergeSeriesPreference = options.Enhance.MergeSeriesPreference;
            if (!(library.CollectionType == CollectionType.Movies.ToString() ||
                  library.CollectionType == CollectionType.TvShows.ToString() &&
                  mergeSeriesPreference == MergeSeriesScopeOption.GlobalScope ||
                  library.CollectionType is null))
            {
                return;
            }

            MergeMultiVersionTask.currentScanLibrary.Value = library;

            var mergeMoviesTask = this.taskManager.ScheduledTasks.FirstOrDefault(t =>
                t.ScheduledTask is MergeMultiVersionTask);

            if (mergeMoviesTask != null && MergeMultiVersionTask.TryBeginTriggeredExecution())
            {
                _ = this.taskManager.Execute(mergeMoviesTask, new TaskOptions());
            }
        }

        private void OnLibraryOptionsUpdated(object sender, GenericEventArgs<Tuple<CollectionFolder, LibraryOptions>> e)
        {
            if (this.Options.Enhance?.MergeMultiVersion != true)
            {
                return;
            }

            var library = e.Argument.Item1;

            if (library.CollectionType == CollectionType.TvShows.ToString() ||
                library.CollectionType is null)
            {
                LibraryService.EnsureLibraryEnabledAutomaticSeriesGrouping();
            }
        }

        private string GetCurrentVersion()
        {
            var releaseTag = GetAssemblyReleaseTag(this.GetType().Assembly);
            if (!string.IsNullOrWhiteSpace(releaseTag))
            {
                return releaseTag;
            }

            var version = this.GetType().Assembly.GetName().Version;
            return version == null ? "未知" : $"v{version.ToString(4)}";
        }

        private StatusItem BuildVersionStatusItem()
        {
            var currentVersion = GetCurrentVersion();
            var latestVersion = this.releaseInfoService.LatestVersion;

            var normalizedCurrent = NormalizeVersionLabel(currentVersion);
            var normalizedLatest = NormalizeVersionLabel(latestVersion);
            var currentText = string.IsNullOrWhiteSpace(currentVersion) ? "未知" : currentVersion;
            var latestText = string.IsNullOrWhiteSpace(latestVersion) ? "加载中" : latestVersion;

            var status = ItemStatus.Unknown;
            var caption = "版本信息";

            if (!string.IsNullOrWhiteSpace(normalizedCurrent) && !string.IsNullOrWhiteSpace(normalizedLatest))
            {
                if (string.Equals(normalizedCurrent, normalizedLatest, StringComparison.OrdinalIgnoreCase))
                {
                    status = ItemStatus.Succeeded;
                    caption = "已是最新版本";
                }
                else
                {
                    status = ItemStatus.Warning;
                    caption = "发现新版本";
                }
            }

            return new StatusItem(
                caption,
                $"当前版本：{currentText}\n最新版本：{latestText}",
                status);
        }

        private static string NormalizeVersionLabel(string version)
        {
            return string.IsNullOrWhiteSpace(version)
                ? string.Empty
                : version.Trim().TrimStart('v', 'V');
        }

        private static string GetAssemblyReleaseTag(Assembly assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            var releaseTagAttribute = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attr => string.Equals(attr.Key, "ReleaseTag", StringComparison.Ordinal));

            return string.IsNullOrWhiteSpace(releaseTagAttribute?.Value) ? null : releaseTagAttribute.Value.Trim();
        }

        public void Dispose()
        {
            this.libraryManager.ItemAdded -= this.OnItemAdded;
            this.libraryManager.ItemRemoved -= this.OnItemRemoved;
            this.userDataManager.UserDataSaved -= this.OnUserDataSaved;
            this.providerManager.RefreshCompleted -= this.OnRefreshCompleted;
            CollectionFolder.LibraryOptionsUpdated -= this.OnLibraryOptionsUpdated;
            this.releaseInfoService.Dispose();
            PrefetchService?.Dispose();
            StrmFileWatcher?.Dispose();
            IntroSkipPlaySessionMonitor?.Dispose();
            if (ReferenceEquals(ReleaseInfoService, this.releaseInfoService))
            {
                ReleaseInfoService = null;
            }
        }

    }
}
