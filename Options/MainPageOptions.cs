using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Options {
    public class MainPageOptions : EditableOptionsBase {
        public enum RefreshModeOption {
            [Description("补全缺失")] Fill,
            [Description("全部替换")] Replace
        }

        public enum UpdateChannelOption {
            Stable,
            Beta
        }

        public override string EditorTitle => "基础设置";

        public override string EditorDescription => string.Empty;

        public GenericItemList ScheduledTaskEntries { get; set; } = new();

        [VisibleCondition(nameof(ShowRefreshQueueStatus), SimpleCondition.IsTrue)]
        [DisplayName("刷新队列")]
        public StatusItem RefreshQueueStatus { get; set; } = new("刷新队列",
            "元数据刷新：0 / 0  · 0 等待\n媒体信息提取：0 / 0  · 0 等待", ItemStatus.Succeeded);

        [Browsable(false)] public bool ShowRefreshQueueStatus { get; set; } = true;

        public LabelItem UpdatePluginProjectUrl { get; set; } =
            new("https://github.com/honue/MediaInfoKeeper") {
                HyperLink = "https://github.com/honue/MediaInfoKeeper",
                Icon = IconNames.open_in_new
            };

        [DisplayName("版本信息")] public StatusItem UpdatePluginVersionStatus { get; set; } = new("版本信息", "当前版本：未知\n最新版本：加载中");

        [DisplayName("更新说明")]
        [Description("始终显示全部 GitHub Releases 的发布记录；预发布版会额外标记为 [Prerelease]。")]
        public string UpdatePluginReleaseHistoryBody { get; set; } = "加载中";

        [DisplayName("启用插件")]
        [Description("关闭后将不执行任何行为。")]
        public bool PlugginEnabled { get; set; } = true;

        [DisplayName("Emby入库扫描延迟（秒）")]
        [Description("控制 Emby 实时入库扫描的等待时间，Emby 默认值 90s。光速入库，不建议小于10s。")]
        [MinValue(5)]
        [MaxValue(90)]
        public int FileChangeRefreshDelaySeconds { get; set; } = 15;

        [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("追更媒体库")]
        [Description("选定触发入库的媒体库范围；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string CatchupLibraries { get; set; } = string.Empty;

        [Browsable(false)] public ScheduledTaskEditorOptions ScheduledTasksEditor { get; set; } = new();

        public void EnsureScheduledTaskEditors() {
            ScheduledTasksEditor ??= new ScheduledTaskEditorOptions();
            ScheduledTasksEditor.RefreshRecentMetadata ??= new RefreshRecentMetadataTaskEditorOptions();
            ScheduledTasksEditor.SubmitTheIntroDbMarkers ??= new SubmitTheIntroDbMarkersTaskEditorOptions();
            ScheduledTasksEditor.ExportExistingMediaInfo ??= new ExportExistingMediaInfoTaskEditorOptions();
            ScheduledTasksEditor.RestoreMediaInfo ??= new RestoreMediaInfoTaskEditorOptions();
            ScheduledTasksEditor.UpdatePlugin ??= new UpdatePluginTaskEditorOptions();
        }

        public void PrepareScheduledTaskEditorForUi() {
            EnsureScheduledTaskEditors();
            ScheduledTasksEditor.LibraryList = LibraryList;
            ScheduledTasksEditor.RefreshRecentMetadata.LibraryList = LibraryList;
            ScheduledTasksEditor.SubmitTheIntroDbMarkers.LibraryList = LibraryList;
            ScheduledTasksEditor.ExportExistingMediaInfo.LibraryList = LibraryList;
            ScheduledTasksEditor.RestoreMediaInfo.LibraryList = LibraryList;
            ScheduledTasksEditor.UpdatePlugin.Initialize();

            ScheduledTaskEntries = BuildScheduledTaskEntries();
        }

        public override IEditObjectContainer CreateEditContainer() {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0) return container;

            var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.EditorItems) {
                var key = item.Name ?? item.Id;
                if (item is EditorText text &&
                    string.Equals(key, nameof(UpdatePluginReleaseHistoryBody), StringComparison.OrdinalIgnoreCase)) {
                    text.IsReadOnly = true;
                    text.MultiLine = true;
                    text.LineCount = 12;
                    text.AllowEmpty = true;
                }

                if (string.IsNullOrEmpty(key)) continue;

                if (!itemLookup.ContainsKey(key)) itemLookup.Add(key, item);
            }

            var groupedItems = new List<EditorBase>();
            var groupIndex = 0;

            void AddGroup(string title, string description, params string[] propertyNames) {
                var items = new List<EditorBase>();
                foreach (var propertyName in propertyNames)
                    if (itemLookup.TryGetValue(propertyName, out var item)) {
                        items.Add(item);
                        itemLookup.Remove(propertyName);
                    }

                if (items.Count == 0) return;

                groupIndex++;
                var group = new EditorGroup(title, items.ToArray(), $"group{groupIndex}", root.Id, null) {
                    Description = description
                };
                groupedItems.Add(group);
            }

            AddGroup("插件", string.Empty,
                nameof(PlugginEnabled),
                nameof(RefreshQueueStatus),
                nameof(FileChangeRefreshDelaySeconds),
                nameof(CatchupLibraries));

            AddGroup("计划任务配置", string.Empty,
                nameof(ScheduledTaskEntries),
                nameof(UpdatePluginVersionStatus),
                nameof(UpdatePluginProjectUrl),
                nameof(UpdatePluginReleaseHistoryBody));

            var remaining = new List<EditorBase>();
            foreach (var item in root.EditorItems) {
                var key = item.Name ?? item.Id;
                if (!string.IsNullOrEmpty(key) && itemLookup.ContainsKey(key)) {
                    remaining.Add(item);
                    itemLookup.Remove(key);
                }
            }

            if (remaining.Count > 0) {
                groupIndex++;
                groupedItems.Add(new EditorGroup("其他", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            if (groupedItems.Count > 0) root.EditorItems = groupedItems.ToArray();

            return container;
        }

        private GenericItemList BuildScheduledTaskEntries() {
            return new GenericItemList(new[] {
                CreateScheduledTaskEntry("更新插件", "main.scheduled.updatePlugin", "main.scheduled.run.updatePlugin"),
                CreateScheduledTaskEntry("刷新媒体元数据", "main.scheduled.refreshRecentMetadata",
                    "main.scheduled.run.refreshRecentMetadata"),
                CreateScheduledTaskEntry("备份媒体信息", "main.scheduled.exportExistingMediaInfo",
                    "main.scheduled.run.exportExistingMediaInfo"),
                CreateScheduledTaskEntry("恢复媒体信息", "main.scheduled.restoreMediaInfo",
                    "main.scheduled.run.restoreMediaInfo"),
                CreateScheduledTaskEntry("共享片头片尾", "main.scheduled.submitTheIntroDbMarkers",
                    "main.scheduled.run.submitTheIntroDbMarkers"),
                CreateScheduledTaskEntry("重启Emby", null, "main.scheduled.run.restartEmby")
            });
        }

        private static GenericListItem CreateScheduledTaskEntry(string primaryText, string commandId, string runCommandId) {
            var item = new GenericListItem {
                PrimaryText = primaryText,
                Button2 = new ButtonItem("执行") {
                    CommandId = runCommandId,
                    Icon = IconNames.play_arrow
                }
            };

            if (!string.IsNullOrEmpty(commandId))
                item.Button1 = new ButtonItem("配置") {
                    CommandId = commandId,
                    Icon = IconNames.settings
                };

            return item;
        }

        public class RefreshRecentMetadataTaskEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [DisplayName("刷新最近入库时间窗口（天）")]
            [Description("仅处理指定天数内入库的条目，0 表示不限制。")]
            [MinValue(0)]
            [MaxValue(3650)]
            public int RefreshRecentMetadataDays { get; set; } = 3;

            [DisplayName("刷新模式")]
            [Description("依据 Emby 媒体库中的设置和元数据提供器，用新的数据更新元数据。")]
            public RefreshModeOption RefreshMetadataMode { get; set; } = RefreshModeOption.Fill;

            [DisplayName("替换现有图像")]
            [Description("基于媒体库选项，将删除全部现有图像，并下载新图像。")]
            public bool ReplaceExistingImages { get; set; } = false;

            [DisplayName("替换现有视频预览缩略图")]
            [Description("如果在媒体库选项中启用此功能，将删除现有视频预览缩略图并生成新的缩略图。")]
            public bool ReplaceExistingVideoPreviewThumbnails { get; set; } = false;

            [DisplayName("允许使用 ffprocess")]
            [Description("Strm 需要截图或提取内嵌信息时，允许执行 ffprocess。")]
            public bool AllowFfProcess { get; set; } = false;

            [DisplayName("跳过首播日期过旧的条目")]
            [Description("任务仍先按入库时间筛选；开启后，如果条目有首播日期且早于入库时间窗口，就不刷新。没有首播日期的条目会继续刷新。")]
            public bool EnablePremiereDateFilter { get; set; } = true;

            [DisplayName("完整刷新已完结剧集")]
            [Description("最近入库命中剧集时，覆盖刷新剧集元数据；如果本次刷新后状态变为完结，再按本任务参数刷新该剧集下全部分集。")]
            public bool RefreshCompletedSeriesEpisodes { get; set; } = true;

            [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("媒体库范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string RefreshRecentMetadataLibraries { get; set; } = string.Empty;

            public override IEditObjectContainer CreateEditContainer() {
                var container = (EditObjectContainer)base.CreateEditContainer();
                var root = container.EditorRoot;
                if (root?.EditorItems == null || root.EditorItems.Length == 0) return container;

                var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in root.EditorItems) {
                    var key = item.Name ?? item.Id;
                    if (!string.IsNullOrEmpty(key) && !itemLookup.ContainsKey(key)) itemLookup.Add(key, item);
                }

                var groupedItems = new List<EditorBase>();
                var groupIndex = 0;

                void AddGroup(string title, string description, params string[] propertyNames) {
                    var items = new List<EditorBase>();
                    foreach (var propertyName in propertyNames)
                        if (itemLookup.TryGetValue(propertyName, out var item)) {
                            items.Add(item);
                            itemLookup.Remove(propertyName);
                        }

                    if (items.Count == 0) return;

                    groupIndex++;
                    groupedItems.Add(new EditorGroup(title, items.ToArray(), $"group{groupIndex}", root.Id, null) {
                        Description = description
                    });
                }

                AddGroup("刷新范围", string.Empty,
                    nameof(RefreshRecentMetadataDays),
                    nameof(RefreshRecentMetadataLibraries),
                    nameof(EnablePremiereDateFilter),
                    nameof(RefreshCompletedSeriesEpisodes));

                AddGroup("刷新参数", string.Empty,
                    nameof(RefreshMetadataMode),
                    nameof(ReplaceExistingImages),
                    nameof(ReplaceExistingVideoPreviewThumbnails),
                    nameof(AllowFfProcess));

                var remaining = new List<EditorBase>();
                foreach (var item in root.EditorItems) {
                    var key = item.Name ?? item.Id;
                    if (!string.IsNullOrEmpty(key) && itemLookup.ContainsKey(key)) {
                        remaining.Add(item);
                        itemLookup.Remove(key);
                    }
                }

                if (remaining.Count > 0) {
                    groupIndex++;
                    groupedItems.Add(new EditorGroup("其他", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
                }

                if (groupedItems.Count > 0) root.EditorItems = groupedItems.ToArray();

                return container;
            }
        }

        public class SubmitTheIntroDbMarkersTaskEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [DisplayName("共享最近入库时间窗口（天）")]
            [Description("仅处理指定天数内入库的电影和剧集，0 表示不限制。")]
            [MinValue(0)]
            [MaxValue(3650)]
            public int SubmitTheIntroDbMarkersDays { get; set; } = 3;

            [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("媒体库范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string SubmitTheIntroDbMarkersLibraries { get; set; } = string.Empty;
        }

        public class ExportExistingMediaInfoTaskEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("备份媒体信息范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string ExportExistingMediaInfoLibraries { get; set; } = string.Empty;
        }

        public class RestoreMediaInfoTaskEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("恢复媒体信息范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string RestoreMediaInfoLibraries { get; set; } = string.Empty;
        }

        public class UpdatePluginTaskEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [DisplayName("GitHub 访问令牌")]
            [Description("设置后使用 Token 获取 Releases，避免未认证请求的限流。")]
            public string GitHubToken { get; set; } = string.Empty;

            [DisplayName("下载前缀")]
            [Description("仅用于插件 Dll 下载，例如 https://ghfast.top 已配置网络代理时通常不需要再设置这里，避免代理链路叠加。")]
            public string DownloadUrlPrefix { get; set; } = string.Empty;

            [Browsable(false)] public List<EditorSelectOption> UpdateChannelList { get; set; } = new();

            [DisplayName("更新频道")]
            [Description("Stable 只拉取最新正式版 Release；Beta 拉取最新 Release，可能是正式版，也可能是预发布版。")]
            [Editor(typeof(EditorSelectSingle), typeof(EditorBase))]
            [SelectItemsSource(nameof(UpdateChannelList))]
            public string UpdateChannel { get; set; } = UpdateChannelOption.Stable.ToString();

            [DisplayName("允许服务器自动重启以便应用插件更新生效")]
            [Description("服务器将仅在空闲期间（此时没有活动用户）重新启动。")]
            public bool RestartEmbyAfterUpdate { get; set; } = false;

            public void Initialize() {
                if (string.IsNullOrWhiteSpace(UpdateChannel)) UpdateChannel = UpdateChannelOption.Stable.ToString();

                UpdateChannelList.Clear();
                foreach (UpdateChannelOption item in Enum.GetValues(typeof(UpdateChannelOption)))
                    UpdateChannelList.Add(new EditorSelectOption {
                        Name = item == UpdateChannelOption.Stable ? "Stable" : "Beta",
                        Value = item.ToString(),
                        IsEnabled = true
                    });
            }

            public override IEditObjectContainer CreateEditContainer() {
                var container = (EditObjectContainer)base.CreateEditContainer();
                var root = container.EditorRoot;
                if (root?.EditorItems == null || root.EditorItems.Length == 0) return container;

                var items = new List<EditorBase>(root.EditorItems.Length);
                var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in root.EditorItems) {
                    var key = item.Name ?? item.Id;
                    items.Add(item);
                    if (!string.IsNullOrEmpty(key) && !itemLookup.ContainsKey(key)) itemLookup.Add(key, item);
                }

                var groupedItems = new List<EditorBase>();
                var groupIndex = 0;

                void AddGroup(string title, string description, params string[] propertyNames) {
                    var groupItems = new List<EditorBase>();
                    foreach (var propertyName in propertyNames)
                        if (itemLookup.TryGetValue(propertyName, out var item)) {
                            groupItems.Add(item);
                            itemLookup.Remove(propertyName);
                        }

                    if (groupItems.Count == 0) return;

                    groupIndex++;
                    var group = new EditorGroup(title, groupItems.ToArray(), $"group{groupIndex}", root.Id, null) {
                        Description = description
                    };
                    groupedItems.Add(group);
                }

                AddGroup("更新插件", "",
                    nameof(GitHubToken),
                    nameof(DownloadUrlPrefix),
                    nameof(UpdateChannel),
                    nameof(RestartEmbyAfterUpdate));

                root.EditorItems = groupedItems.Count > 0 ? groupedItems.ToArray() : items.ToArray();
                return container;
            }
        }

        public class ScheduledTaskEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("刷新媒体元数据")]
            public RefreshRecentMetadataTaskEditorOptions RefreshRecentMetadata { get; set; } =
                new();

            [DisplayName("共享片头片尾")]
            public SubmitTheIntroDbMarkersTaskEditorOptions SubmitTheIntroDbMarkers { get; set; } =
                new();

            [DisplayName("备份媒体信息")]
            public ExportExistingMediaInfoTaskEditorOptions ExportExistingMediaInfo { get; set; } =
                new();

            [DisplayName("恢复媒体信息")]
            public RestoreMediaInfoTaskEditorOptions RestoreMediaInfo { get; set; } =
                new();

            [DisplayName("更新插件")] public UpdatePluginTaskEditorOptions UpdatePlugin { get; set; } = new();
        }
    }
}
