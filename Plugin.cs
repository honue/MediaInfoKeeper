using System;
using System.Collections.Generic;
using System.IO;
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

        private bool currentPersistMediaInfo;

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

            this.currentPersistMediaInfo = this.Options.PersistMediaInfoEnabled;

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
            return base.OnBeforeShowUI(options);
        }

        protected override bool OnOptionsSaving(PluginConfiguration options)
        {
            options.ScopedLibraries = NormalizeScopedLibraries(options.ScopedLibraries);
            return base.OnOptionsSaving(options);
        }

        /// <summary>应用配置变更并更新缓存标记。</summary>
        protected override void OnOptionsSaved(PluginConfiguration options)
        {
            this.currentPersistMediaInfo = options.PersistMediaInfoEnabled;

            this.logger.Info($"{this.Name} 配置已更新。");
            this.logger.Info($"PersistMediaInfoEnabled 设置为 {options.PersistMediaInfoEnabled}");
            this.logger.Info($"MediaInfoJsonRootFolder 设置为 {(string.IsNullOrEmpty(options.MediaInfoJsonRootFolder) ? "EMPTY" : options.MediaInfoJsonRootFolder)}");
            this.logger.Info($"DeleteMediaInfoJsonOnRemove 设置为 {options.DeleteMediaInfoJsonOnRemove}");
            this.logger.Info($"ScopedLibraries 设置为 {(string.IsNullOrEmpty(options.ScopedLibraries) ? "EMPTY" : options.ScopedLibraries)}");
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
                    var restored = await MediaInfoService.DeserializeMediaInfo(e.Item, directoryService, "OnItemAdded", true).ConfigureAwait(false);

                    if (!restored)
                    {
                        // 恢复失败时先触发媒体信息提取，再写入 JSON。
                        this.logger.Info("恢复失败，开始提取 MediaInfo");
                        // 构建用于媒体信息提取的刷新参数与库选项。
                        var refreshOptions = MediaInfoService.GetMediaInfoRefreshOptions();
                        var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(e.Item);
                        var libraryOptions = this.libraryManager.GetLibraryOptions(e.Item);
                        var dummyLibraryOptions = LibraryService.CopyLibraryOptions(libraryOptions);

                        // 禁用本地元数据与保存器，避免额外写入。
                        dummyLibraryOptions.DisabledLocalMetadataReaders = new[] { "Nfo" };
                        dummyLibraryOptions.MetadataSavers = Array.Empty<string>();

                        // 关闭元数据与图片抓取，仅保留媒体信息提取。
                        foreach (var option in dummyLibraryOptions.TypeOptions)
                        {
                            option.MetadataFetchers = Array.Empty<string>();
                            option.ImageFetchers = Array.Empty<string>();
                        }

                        // 触发一次刷新以提取 MediaInfo。
                        e.Item.DateLastRefreshed = new DateTimeOffset();

                        await this.providerManager
                            .RefreshSingleItem(e.Item, refreshOptions, collectionFolders, dummyLibraryOptions, CancellationToken.None)
                            .ConfigureAwait(false);

                        // 提取完成后写入 JSON。
                        this.logger.Info("MediaInfo 提取完成，写入 JSON");
                        _ = MediaInfoService.SerializeMediaInfo(e.Item.InternalId, directoryService, true, "OnItemAdded WriteNewJson");
                    }
                    else
                    {
                        this.logger.Info("JSON 恢复成功，跳过提取");
                    }
                }
                else
                {
                    // 已有 MediaInfo 时直接覆盖写入最新 JSON。
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
            if (!this.Options.DeleteMediaInfoJsonOnRemove || !this.Options.PersistMediaInfoEnabled)
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
            MediaInfoService.DeleteMediaInfoJson(e.Item, directoryService, "Item Removed Event");
        }
    }
}


