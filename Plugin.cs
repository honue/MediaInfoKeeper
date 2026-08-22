using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
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
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.View;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Provider;
using MediaInfoKeeper.ScheduledTask;
using MediaInfoKeeper.Services;
using MediaInfoKeeper.Services.IntroSkip;
using MediaInfoKeeper.Store;
using MediaInfoKeeper.Web;
using static MediaInfoKeeper.Options.EnhanceOptions;
using JsonSerializerOptions = System.Text.Json.JsonSerializerOptions;

namespace MediaInfoKeeper {
    /// <summary>
    ///     The plugin.
    /// </summary>
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages, IDisposable {
        public const string PluginName = "MediaInfoKeeper";
        public const string TaskCategoryName = "Auto-MediaInfoKeeper";

        public static Plugin Instance { get; private set; }
        public static MediaInfoService MediaInfoService { get; private set; }
        public static MetaDataService MetaDataService { get; private set; }

        public static ChaptersStore ChaptersStore { get; private set; }
        public static MediaSourceInfoStore MediaSourceInfoStore { get; private set; }
        public static EmbeddedInfoStore EmbeddedInfoStore { get; private set; }
        public static LibraryService LibraryService { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static IntroSkipChapterApi IntroSkipChapterApi { get; private set; }
        public static IntroSkipPlaySessionMonitor IntroSkipPlaySessionMonitor { get; private set; }
        public static PrefetchService PrefetchService { get; private set; }
        public static StrmFileWatcher StrmFileWatcher { get; private set; }
        public static ExternalFiles ExternalFiles { get; private set; }
        public static DanmuService DanmuService { get; private set; }
        internal static ReleaseInfoService ReleaseInfoService { get; private set; }

        private readonly Guid id = new("874D7056-072D-43A4-16DD-BC32665B9563");
        private List<IPluginUIPageController> pages;

        private readonly ILibraryManager libraryManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly IUserManager userManager;
        private readonly IUserDataManager userDataManager;
        private readonly ISessionManager sessionManager;
        private readonly IMediaMountManager mediaMountManager;
        private readonly ITaskManager taskManager;
        private readonly ReleaseInfoService releaseInfoService;
        private readonly SemaphoreSlim itemAddedSemaphore;

        internal static IProviderManager ProviderManager { get; private set; }
        internal static IFileSystem FileSystem { get; private set; }
        internal static ILibraryManager LibraryManager { get; private set; }
        internal static IHttpClient SharedHttpClient { get; private set; }
        internal static ILogger SharedLogger { get; private set; }
        internal IApplicationHost AppHost { get; }

        internal IItemRepository ItemRepository { get; }

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
            IFileSystem fileSystem) {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info($"插件 {Name} 正在加载");

            AppHost = applicationHost;
            this.libraryManager = libraryManager;
            this.libraryMonitor = libraryMonitor;
            this.providerManager = providerManager;
            ItemRepository = itemRepository;
            this.fileSystem = fileSystem;
            this.userManager = userManager;
            this.userDataManager = userDataManager;
            this.sessionManager = sessionManager;
            this.mediaMountManager = mediaMountManager;
            taskManager = applicationHost.Resolve<ITaskManager>();
            releaseInfoService = new ReleaseInfoService(httpClient, Logger, () => Options);
            ReleaseInfoService = releaseInfoService;
            ProviderManager = providerManager;
            FileSystem = fileSystem;
            LibraryManager = libraryManager;
            SharedHttpClient = httpClient;
            SharedLogger = Logger;

            OptionsStore = new PluginOptionsStore(applicationHost, Logger, Name,
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

            var initialOptions = Options;
            PersistOptionsOnStartup(initialOptions);
            ConfigureRunners(initialOptions);
            var itemAddedMaxConcurrent = Math.Max(1, initialOptions?.MediaInfo?.MaxConcurrentCount ?? 1);
            itemAddedSemaphore = new SemaphoreSlim(itemAddedMaxConcurrent, itemAddedMaxConcurrent);
            PatchManager.Initialize(Logger, initialOptions, activityManager);

            PlugginEnabled = initialOptions.MainPage?.PlugginEnabled ?? true;
            LogOptionsSnapshot(initialOptions, "已加载");

            LibraryService = new LibraryService(libraryManager, fileSystem, userManager, userDataManager,
                mediaMountManager);
            MediaInfoService = new MediaInfoService(libraryManager, mediaSourceManager, fileSystem);
            MetaDataService = new MetaDataService(providerManager);
            ChaptersStore = new ChaptersStore(itemRepository, fileSystem, jsonSerializer);
            MediaSourceInfoStore = new MediaSourceInfoStore(libraryManager, itemRepository, fileSystem, jsonSerializer);
            EmbeddedInfoStore = new EmbeddedInfoStore(jsonSerializer);
            DanmuService = new DanmuService(logManager, httpClient);

            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager);
            IntroSkipChapterApi = new IntroSkipChapterApi(libraryManager, itemRepository, Logger);
            IntroScanRunner.Initialize(logManager, libraryManager, fileSystem);
            IntroSkipPlaySessionMonitor = new IntroSkipPlaySessionMonitor(
                libraryManager, userManager, sessionManager, Logger);
            PrefetchService = new PrefetchService(libraryManager, sessionManager, Logger);
            StrmFileWatcher = new StrmFileWatcher(libraryMonitor, libraryManager, LibraryService, Logger);
            PluginWebResourceLoader.Initialize(serverConfigurationManager);
            PrefetchService.Initialize();
            releaseInfoService.Start();

            if (Options.IntroSkip?.EnableIntroMarker == true ||
                Options.IntroSkip?.EnableCreditsMarker == true) {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(Options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(Options.IntroSkip.UserScope);
            }

            ConfigureStrmFileWatcher();

            this.libraryManager.ItemAdded += OnItemAdded;
            this.libraryManager.ItemRemoved += OnItemRemoved;
            this.userDataManager.UserDataSaved += OnUserDataSaved;
            this.providerManager.RefreshCompleted += OnRefreshCompleted;
            CollectionFolder.LibraryOptionsUpdated += OnLibraryOptionsUpdated;

            Logger.Info($"插件 {Name} 加载完成");
        }

        public override string Description => "Persist/restore MediaInfo to speed up first playback.";

        public override Guid Id => id;

        public sealed override string Name => PluginName;

        public PluginConfiguration Options {
            get {
                var options = OptionsStore.GetOptions();
                options.MainPage ??= new MainPageOptions();
                options.MainPage.PrepareScheduledTaskEditorForUi();
                options.MediaInfo ??= new MediaInfoOptions();
                options.IntroSkip ??= new IntroSkipOptions();
                options.MetaData ??= new MetaDataOptions();
                options.MetaData.EnsureScraperEditors();
                return options;
            }
        }

        public ILogger Logger { get; }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage() {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Resources.ThumbImage.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers {
            get {
                if (pages == null)
                    pages = new List<IPluginUIPageController> {
                        new MainPageController(AppHost, GetPluginInfo(), MainPageOptionsStore,
                            MediaInfoOptionsStore,
                            IntroSkipOptionsStore, NetWorkOptionsStore,
                            EnhanceOptionsStore, MetaDataOptionsStore
#if DEBUG
                            , this.DebugOptionsStore
#endif
                        )
                    };

                return pages.AsReadOnly();
            }
        }

        internal void PrepareOptionsForUi(PluginConfiguration options) {
            if (options == null) return;

            options.MainPage ??= new MainPageOptions();
            options.MediaInfo ??= new MediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.GetNetWorkOptions();
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();

            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif
            options.IntroSkip.Initialize();
            options.MediaInfo.Initialize();
            effectiveUpdatePluginOptions.Initialize();
            options.Enhance.Initialize();
            options.MetaData.Initialize();

            var list = LibraryService.BuildLibrarySelectOptions();
            NormalizeScopedLibraryOptions(options);
            options.MainPage.LibraryList = list;
            options.IntroSkip.LibraryList = list;
            options.MainPage.UpdatePluginVersionStatus = BuildVersionStatusItem();
            options.MainPage.UpdatePluginReleaseHistoryBody =
                string.IsNullOrWhiteSpace(releaseInfoService.HistoryBody)
                    ? "加载中"
                    : releaseInfoService.HistoryBody;
            options.MainPage.PrepareScheduledTaskEditorForUi();
        }

        internal bool HandleOptionsSaving(PluginConfiguration options) {
            if (options?.MainPage == null) return true;

            FinalizeOptionsForPersistence(options);
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();

            var netWorkOptions = options.GetNetWorkOptions();
            if (!LocalDiscoveryAddress.TryValidateConfiguredValue(
                    netWorkOptions.CustomLocalDiscoveryAddress,
                    out var normalizedDiscoveryAddress,
                    out var validationError)) {
                Logger.Warn("自定义本地发现地址校验失败：{0}", validationError);
                return false;
            }

            netWorkOptions.CustomLocalDiscoveryAddress = normalizedDiscoveryAddress;
            return true;
        }

        /// <summary>应用配置变更并更新缓存标记。</summary>
        internal void HandleOptionsSaved(PluginConfiguration options) {
            if (options == null) return;

            options.MainPage ??= new MainPageOptions();
            options.MediaInfo ??= new MediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            if (string.IsNullOrWhiteSpace(effectiveUpdatePluginOptions.UpdateChannel))
                effectiveUpdatePluginOptions.UpdateChannel = MainPageOptions.UpdateChannelOption.Stable.ToString();

            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif
            var netWorkOptions = options.GetNetWorkOptions();

            PlugginEnabled = options.MainPage.PlugginEnabled;

            LogOptionsSnapshot(options, "已更新");

            PatchManager.Configure(options);

            if (options.IntroSkip.EnableIntroMarker || options.IntroSkip.EnableCreditsMarker) {
                IntroSkipPlaySessionMonitor.Initialize();
                IntroSkipPlaySessionMonitor.UpdateLibraryPathsInScope(options.IntroSkip.LibraryScope);
                IntroSkipPlaySessionMonitor.UpdateUsersInScope(options.IntroSkip.UserScope);
            }
            else {
                IntroSkipPlaySessionMonitor.Dispose();
            }

            ConfigureStrmFileWatcher();
        }

        private static void ConfigureRunners(PluginConfiguration options) {
            var safeOptions = options ?? new PluginConfiguration();
            safeOptions.MediaInfo ??= new MediaInfoOptions();
            safeOptions.MetaData ??= new MetaDataOptions();
            safeOptions.IntroSkip ??= new IntroSkipOptions();

            MediaInfoRunner.Configure(safeOptions.MediaInfo.MaxConcurrentCount);
            MetaDataRunner.Configure(safeOptions.MetaData.MaxConcurrentCount);
            IntroScanRunner.Configure(safeOptions.IntroSkip.IntroDetectionMaxConcurrentCount);
        }

        private void ConfigureStrmFileWatcher() {
            var safeOptions = OptionsStore.GetOptions() ?? new PluginConfiguration();
            safeOptions.MainPage ??= new MainPageOptions();
            safeOptions.MediaInfo ??= new MediaInfoOptions();

            StrmFileWatcher?.Configure(PlugginEnabled, safeOptions.MainPage.FileChangeRefreshDelaySeconds);
        }

        private void PersistOptionsOnStartup(PluginConfiguration options) {
            if (options == null) return;

            FinalizeOptionsForPersistence(options);
            OptionsStore.SetOptionsSilently(options);
        }

        private void FinalizeOptionsForPersistence(PluginConfiguration options) {
            if (options == null) return;

            options.MainPage ??= new MainPageOptions();
            options.MainPage.ScheduledTasksEditor ??= new MainPageOptions.ScheduledTaskEditorOptions();
            options.MediaInfo ??= new MediaInfoOptions();
            options.IntroSkip ??= new IntroSkipOptions();
            options.GetNetWorkOptions();
            options.Enhance ??= new EnhanceOptions();
            options.MetaData ??= new MetaDataOptions();
            options.MetaData.EnsureScraperEditors();
#if DEBUG
            options.Debug ??= new DebugOptions();
#endif

            var effectiveUpdatePluginOptions = options.GetEffectiveUpdatePluginOptions();
            effectiveUpdatePluginOptions.Initialize();
            options.MainPage.ScheduledTasksEditor.UpdatePlugin = effectiveUpdatePluginOptions;

            NormalizeScopedLibraryOptions(options);
        }

        private void NormalizeScopedLibraryOptions(PluginConfiguration options) {
            if (options?.MainPage == null) return;

            var scheduledTasksEditor = options.MainPage.ScheduledTasksEditor;
            if (scheduledTasksEditor != null) {
                scheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries =
                    NormalizeScopedLibraries(scheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries);
                scheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries =
                    NormalizeScopedLibraries(scheduledTasksEditor.ExportExistingMediaInfo
                        .ExportExistingMediaInfoLibraries);
                scheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries =
                    NormalizeScopedLibraries(scheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries);
            }

            if (options.IntroSkip != null) options.IntroSkip.LibraryScope = NormalizeScopedLibraries(options.IntroSkip.LibraryScope);
        }

        private void LogOptionsSnapshot(PluginConfiguration options, string action) {
            var optionsFilePath = OptionsStore.OptionsFilePath;
            if (!File.Exists(optionsFilePath)) {
                Logger.Debug("{0} 配置{1}: 配置文件不存在 {2}", Name, action, optionsFilePath);
                return;
            }

            try {
                var json = File.ReadAllText(optionsFilePath);
                var node = JsonNode.Parse(json);
                RedactSecret(node, nameof(MainPageOptions.UpdatePluginTaskEditorOptions.GitHubToken));
                RedactSecret(node, nameof(NetWorkOptions.AlternativeTmdbApiKey));
                RedactSecret(node, nameof(MetaDataOptions.TheIntroDbEditorOptions.ApiKey));
                Logger.Debug("{0} 配置{1}: {2}", Name, action,
                    node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ??
                    string.Empty);
            }
            catch (Exception ex) {
                Logger.Debug("{0} 配置{1}: 配置 JSON 读取或解析失败 {2}", Name, action, ex.Message);
            }
        }

        private static void RedactSecret(JsonNode node, string propertyName) {
            if (node is JsonObject obj) {
                if (obj.ContainsKey(propertyName)) obj[propertyName] = "***";

                foreach (var child in obj.Select(pair => pair.Value).ToList()) RedactSecret(child, propertyName);
            }
            else if (node is JsonArray array) {
                foreach (var child in array) RedactSecret(child, propertyName);
            }
        }

        private string NormalizeScopedLibraries(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in libraryManager.GetVirtualFolders()) {
                if (folder == null) continue;

                if (!string.IsNullOrWhiteSpace(folder.ItemId)) lookup[folder.ItemId] = folder.ItemId;

                if (!string.IsNullOrWhiteSpace(folder.Id)) lookup[folder.Id] = folder.ItemId;

                if (!string.IsNullOrWhiteSpace(folder.Guid)) lookup[folder.Guid] = folder.ItemId;

                if (!string.IsNullOrWhiteSpace(folder.Name)) lookup[folder.Name.Trim()] = folder.ItemId;
            }

            var tokens = raw.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var normalized = new List<string>();
            var invalid = new List<string>();
            foreach (var token in tokens) {
                var value = token.Trim();
                if (string.IsNullOrEmpty(value)) continue;

                if (lookup.TryGetValue(value, out var mapped)) {
                    if (!normalized.Contains(mapped, StringComparer.OrdinalIgnoreCase)) normalized.Add(mapped);
                }
                else {
                    invalid.Add(value);
                }
            }

            if (invalid.Count > 0) Logger.Warn("已移除失效媒体库范围配置: {0}", string.Join(",", invalid));

            return string.Join(",", normalized);
        }

        /// <summary>处理新入库条目，按配置执行持久化或恢复。</summary>
        private void OnItemAdded(object sender, ItemChangeEventArgs e) {
            var item = e?.Item;
            if (item == null) return;

            var itemId = item.InternalId;
            var itemDisplayName = item.FileName ?? item.Path;

            _ = Task.Run(async () => {
                var semaphoreHeld = false;
                try {
                    await itemAddedSemaphore.WaitAsync().ConfigureAwait(false);
                    semaphoreHeld = true;

                    if (!PlugginEnabled)
                        // 未启用持久化，直接跳过。
                        return;

                    if (!(item is Video) && !(item is Audio)) {
                        // 仅处理音视频条目,补刷 Series Season 等等。
                        if (item is Folder && IsItemAddedRefreshProviderEnabled(item)) {
                            _ = MetaDataRunner.RefreshMetaDataAsync(itemId, priority: RefreshPriority.Highest,
                                allowFfProcess: true);
                        }

                        return;
                    }

                    Logger.Info($"新入库事件 {item.FileName ?? item.Path}");

                    // 判断当前条目是否已有 MediaInfo。
                    var hasMediaInfo = MediaInfoService.HasMediaInfo(item);
                    if (!hasMediaInfo) {
                        // 优先尝试从 JSON 恢复，减少首次提取耗时。
                        Logger.Debug("尝试从 JSON 恢复 MediaInfo");
                        var restoreResult = MediaSourceInfoStore.ApplyToItem(item);
                        var shouldRefreshAfterRestore =
                            restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Failed;

                        // 如果不存在Json文件，则使用ffprobe 提取一次
                        if (shouldRefreshAfterRestore) {
                            if (!IsItemAddedMediaInfoProviderEnabled(item))
                                Logger.Info($"已关闭入库提取媒体信息，跳过提取 item={item.FileName ?? item.Path}");
                            else
                                try {
                                    // 恢复失败时先触发媒体信息提取，再写入 JSON。
                                    var extracted = await MediaInfoRunner.ExtractMediaInfoAsync(itemId, "入库媒体信息",
                                            cancellationToken: CancellationToken.None,
                                            priority: RefreshPriority.Highest)
                                        .ConfigureAwait(false);
                                    if (!extracted) Logger.Info($"入库媒体信息: 提取失败 item={itemDisplayName}");
                                }
                                catch (Exception extractEx) {
                                    Logger.Error($"入库媒体信息: 提取异常 item={itemDisplayName}");
                                    Logger.Error(extractEx.Message);
                                    Logger.Debug(extractEx.StackTrace);
                                }
                        }
                        // 使用Json媒体信息数据，恢复成功后触发当前条目刷新。
                        else if (restoreResult == MediaInfoDocument.MediaInfoRestoreResult.Restored) {
                            var itemPath = item.Path ?? item.ContainingFolderPath ?? item.InternalId.ToString();
                            Logger.Info($"入库媒体信息: JSON 恢复成功 item={itemPath}");

                            if (item is Video) ChaptersStore.ApplyToItem(item);

                            if (item is Audio) EmbeddedInfoStore.ApplyToItem(item);
                        }
                    }
                    // 所有需要媒体信息的任务启动完成后，后台等待媒体信息队列清空，再刷新元数据。
                    await MediaInfoRunner.WaitForItemFinishAsync(itemId, CancellationToken.None)
                            .ConfigureAwait(false);

                    //释放入库信号量
                    itemAddedSemaphore.Release();
                    semaphoreHeld =  false;

                    if (IsItemAddedRefreshProviderEnabled(item)) {
                        await MetaDataRunner.RefreshMetaDataAsync(itemId, priority: RefreshPriority.Highest, allowFfProcess: true);
                    }

                    // 收藏通知和扫描收藏片头
                    if (item is Episode newEpisode && newEpisode.ExtraType == null) {
                        var series = LibraryService.GetSeries(newEpisode.SeriesId);
                        if (series == null) {
                            Logger.Info($"收藏入库通知跳过: 未找到所属剧集，episodeId={newEpisode.InternalId}");
                        }
                        else {
                            var users = LibraryService.GetFavoriteUsersBySeriesId(series.InternalId);
                            if (users.Count != 0) {
                                // 有人收藏，开始执行扫描收藏媒体信息和片头，避免重复，判断未开启媒体库入库扫描
                                var canScanIntro = Options.IntroSkip?.ScanIntroOnFavorite == true &&
                                                   !IsItemAddedIntroScanProviderEnabled(newEpisode);
                                if (canScanIntro)
                                    _ = IntroScanRunner.ScanEpisodeAsync(newEpisode, "收藏入库",
                                        priority: RefreshPriority.High);

                                // 有人收藏，通知收藏入库
                                Logger.Info($"收藏入库事件: 剧集={series.Name} {newEpisode.Name}, 收藏用户={string.Join(", ", users)}");
                                var sentCount = NotificationApi.LibraryNewSendNotification(series, newEpisode, users);
                                if (sentCount > 0) Logger.Info($"已发送入库通知: 剧集={series.Name} {newEpisode.Name}, 通知用户数={sentCount}");
                            }
                            else {
                                Logger.Debug($"收藏入库通知跳过: 剧集={series.Name}，无收藏用户");
                            }
                        }
                    }

                    // 入库加入扫描片头队列
                    if (IsItemAddedIntroScanProviderEnabled(item) && item is Episode episode)
                        _ = IntroScanRunner.ScanEpisodeAsync(episode, "入库片头扫描", priority: RefreshPriority.High);

                }
                catch (Exception ex) {
                    // 记录异常，避免影响库事件流程。
                    Logger.Error(ex.Message);
                    Logger.Debug(ex.StackTrace);
                }
                finally {
                    if (semaphoreHeld) itemAddedSemaphore.Release();
                }
            });
        }

        /// <summary>判断条目所属媒体库是否启用了 MediaInfoKeeper 入库刮削 Provider。 </summary>
        private bool IsItemAddedRefreshProviderEnabled(BaseItem item) {
            return IsItemAddedProviderEnabled(item, ItemAddedRefreshProvider.ProviderName);
        }

        /// <summary>判断条目所属媒体库是否启用入库 MediaInfo 提取 Provider。</summary>
        private bool IsItemAddedMediaInfoProviderEnabled(BaseItem item) {
            return IsItemAddedProviderEnabled(item, ItemAddedMediaInfoProvider.ProviderName);
        }

        /// <summary>判断条目所属媒体库是否启用入库片头扫描 Provider。</summary>
        private bool IsItemAddedIntroScanProviderEnabled(BaseItem item) {
            return item is Episode &&
                   IsItemAddedProviderEnabled(item, ItemAddedIntroScanProvider.ProviderName);
        }

        private bool IsItemAddedProviderEnabled(BaseItem item, string providerName) {
            if (item == null || item.InternalId <= 0 || string.IsNullOrWhiteSpace(providerName)) {
                return false;
            }

            var libraryOptions = libraryManager.GetLibraryOptions(item);
            var enabled = item.IsLocalMetadataReaderEnabled(libraryOptions, providerName);
            if (!enabled) {
                Logger.Debug("媒体库入库 Provider 已禁用: provider={0}, item={1}",
                    providerName,
                    item.FileName ?? item.Path ?? item.Name);
            }

            return enabled;
        }

        /// <summary> 收藏喜爱事件处 </summary>
        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e) {
            try {
                if (!PlugginEnabled) return;

                var item = e.Item;
                var userData = e.UserData;
                if (item == null || userData == null) return;

                if (item.ExtraType != null) return;

                if (!userData.IsFavorite) return;

                var userName = e.User?.Name ?? "unknown";
                Logger.Info($"收藏事件: 用户={userName}, 条目={item.FileName ?? item.Path ?? item.Id.ToString()}");

                var canScanIntro = Options.IntroSkip?.ScanIntroOnFavorite == true &&
                                   (item is Episode || item is Season || item is Series);

                if (!canScanIntro) return;

                if (canScanIntro) {
                    var episodes = LibraryService.GetSeriesEpisodesFromItem(item);
                    if (episodes.Count > 0)
                        foreach (var seriesEpisode in episodes)
                            _ = IntroScanRunner.ScanEpisodeAsync(seriesEpisode, "OnFavorite",
                                priority: RefreshPriority.High);
                    else
                        Logger.Info("OnFavorite 片头扫描跳过: 未找到系列条目");
                }
            }
            catch (Exception ex) {
                Logger.Error("收藏事件处理异常");
                Logger.Error(ex.Message);
                Logger.Debug(ex.StackTrace);
            }
        }

        /// <summary>条目移除且非恢复模式时，删除已持久化的 JSON。</summary>
        private void OnItemRemoved(object sender, ItemChangeEventArgs e) {
            Logger.Info($"{e.Item.Path} 删除媒体事件");
            // 未开启删除开关时直接跳过。
            if (!Options.MediaInfo.DeleteMediaInfoJsonOnRemove || !Options.MainPage.PlugginEnabled) return;

            if (!(e.Item is Video) && !(e.Item is Audio)) return;

            Logger.Info("同步删除 媒体信息 Json");
            MediaInfoDocument.DeleteMediaInfoJson(e.Item, new DirectoryService(Logger, fileSystem),
                "Item Removed Event");
        }

        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e) {
            if (libraryManager.IsScanRunning) return;

            var options = Options;
            var item = e.Argument.Item;
            if (!(options.Enhance?.MergeMultiVersion == true && item.IsTopParent)) return;

            if (MergeMultiVersionTask.IsInternalRefresh(item.InternalId)) return;

            var library = e.Argument.CollectionFolders.OfType<CollectionFolder>().FirstOrDefault();

            if (library == null) return;

            var mergeSeriesPreference = options.Enhance.MergeSeriesPreference;
            if (!(library.CollectionType == CollectionType.Movies.ToString() ||
                  (library.CollectionType == CollectionType.TvShows.ToString() &&
                   mergeSeriesPreference == MergeSeriesScopeOption.GlobalScope) ||
                  library.CollectionType is null))
                return;

            MergeMultiVersionTask.currentScanLibrary.Value = library;

            var mergeMoviesTask = taskManager.ScheduledTasks.FirstOrDefault(t =>
                t.ScheduledTask is MergeMultiVersionTask);

            if (mergeMoviesTask != null && MergeMultiVersionTask.TryBeginTriggeredExecution())
                _ = taskManager.Execute(mergeMoviesTask, new TaskOptions());
        }

        private void OnLibraryOptionsUpdated(object sender, GenericEventArgs<Tuple<CollectionFolder, LibraryOptions>> e) {
            if (Options.Enhance?.MergeMultiVersion != true) return;

            var library = e.Argument.Item1;

            if (library.CollectionType == CollectionType.TvShows.ToString() ||
                library.CollectionType is null)
                LibraryService.EnsureLibraryEnabledAutomaticSeriesGrouping();
        }

        private string GetCurrentVersion() {
            var releaseTag = GetAssemblyReleaseTag(GetType().Assembly);
            if (!string.IsNullOrWhiteSpace(releaseTag)) return releaseTag;

            var version = GetType().Assembly.GetName().Version;
            return version == null ? "未知" : $"v{version.ToString(4)}";
        }

        private StatusItem BuildVersionStatusItem() {
            var currentVersion = GetCurrentVersion();
            var latestVersion = releaseInfoService.LatestVersion;

            var normalizedCurrent = NormalizeVersionLabel(currentVersion);
            var normalizedLatest = NormalizeVersionLabel(latestVersion);
            var currentText = string.IsNullOrWhiteSpace(currentVersion) ? "未知" : currentVersion;
            var latestText = string.IsNullOrWhiteSpace(latestVersion) ? "加载中" : latestVersion;

            var status = ItemStatus.Unknown;
            var caption = "版本信息";

            if (!string.IsNullOrWhiteSpace(normalizedCurrent) && !string.IsNullOrWhiteSpace(normalizedLatest)) {
                if (string.Equals(normalizedCurrent, normalizedLatest, StringComparison.OrdinalIgnoreCase)) {
                    status = ItemStatus.Succeeded;
                    caption = "已是最新版本";
                }
                else {
                    status = ItemStatus.Warning;
                    caption = "发现新版本";
                }
            }

            return new StatusItem(
                caption,
                $"当前版本：{currentText}\n最新版本：{latestText}",
                status);
        }

        private static string NormalizeVersionLabel(string version) {
            return string.IsNullOrWhiteSpace(version)
                ? string.Empty
                : version.Trim().TrimStart('v', 'V');
        }

        private static string GetAssemblyReleaseTag(Assembly assembly) {
            if (assembly == null) return null;

            var releaseTagAttribute = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attr => string.Equals(attr.Key, "ReleaseTag", StringComparison.Ordinal));

            return string.IsNullOrWhiteSpace(releaseTagAttribute?.Value) ? null : releaseTagAttribute.Value.Trim();
        }

        public void Dispose() {
            libraryManager.ItemAdded -= OnItemAdded;
            libraryManager.ItemRemoved -= OnItemRemoved;
            userDataManager.UserDataSaved -= OnUserDataSaved;
            providerManager.RefreshCompleted -= OnRefreshCompleted;
            CollectionFolder.LibraryOptionsUpdated -= OnLibraryOptionsUpdated;
            releaseInfoService.Dispose();
            PrefetchService?.Dispose();
            StrmFileWatcher?.Dispose();
            IntroSkipPlaySessionMonitor?.Dispose();
            if (ReferenceEquals(ReleaseInfoService, releaseInfoService)) ReleaseInfoService = null;
        }
    }
}
