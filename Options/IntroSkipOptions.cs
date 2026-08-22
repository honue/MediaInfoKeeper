using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Options {
    public class IntroSkipOptions : EditableOptionsBase {
        public enum SubsequentMarkerMode {
            CurrentOnly,
            FillMissing,
            Overwrite
        }

        public override string EditorTitle => "片头片尾";

        public override string EditorDescription => string.Empty;

        [DisplayName("启用 Strm 片头检测解锁")]
        [Description("开启后允许 .strm 参与 Emby 原生片头指纹探测。")]
        public bool UnlockIntroSkip { get; set; } = true;

        [DisplayName("收藏入库时扫描片头")]
        [Description("触发对应收藏媒体片头检测，同时入库收藏剧集时，提取媒体信息，扫描片头。")]
        public bool ScanIntroOnFavorite { get; set; } = true;

        [DisplayName("过滤普通章节")]
        [Description("阻止 Emby/ffprobe 写入Chapter 01/02 等无效章节。")]
        public bool FilterPlainChapters { get; set; } = true;

        [DisplayName("启用片头打标")]
        [Description("根据播放行为自动标记片头。")]
        public bool EnableIntroMarker { get; set; } = false;

        [Browsable(false)] public List<EditorSelectOption> SubsequentMarkerModeList { get; set; } = new();

        [DisplayName("后续片头设置")]
        [Description("按当前季补齐；可只写本集、补全缺失，或覆盖插件已写入的后续标记。当前集和后续剧集中的 Emby 系统片头标记都不会更新。")]
        [Editor(typeof(EditorSelectSingle), typeof(EditorBase))]
        [SelectItemsSource(nameof(SubsequentMarkerModeList))]
        [VisibleCondition(nameof(EnableIntroMarker), SimpleCondition.IsTrue)]
        public string IntroMarkerMode { get; set; } = SubsequentMarkerMode.FillMissing.ToString();

        [DisplayName("启用片尾打标")]
        [Description("根据播放行为自动标记片尾。")]
        public bool EnableCreditsMarker { get; set; } = false;

        [DisplayName("后续片尾设置")]
        [Description("按当前季补齐；可只写本集、补全缺失，或覆盖插件已写入的后续标记。当前集和后续剧集中的 Emby 系统片尾标记都不会更新。")]
        [Editor(typeof(EditorSelectSingle), typeof(EditorBase))]
        [SelectItemsSource(nameof(SubsequentMarkerModeList))]
        [VisibleCondition(nameof(EnableCreditsMarker), SimpleCondition.IsTrue)]
        public string CreditsMarkerMode { get; set; } = SubsequentMarkerMode.FillMissing.ToString();

        [DisplayName("最大片头时长(秒)")]
        [Description("超过此时间不再认为是片头区间。")]
        [VisibleCondition(nameof(EnableIntroMarker), SimpleCondition.IsTrue)]
        [MinValue(10)]
        [MaxValue(600)]
        [Required]
        public int MaxIntroDurationSeconds { get; set; } = 180;

        [DisplayName("最大片尾时长(秒)")]
        [Description("距结尾小于该时长时可标记片尾。")]
        [VisibleCondition(nameof(EnableCreditsMarker), SimpleCondition.IsTrue)]
        [MinValue(10)]
        [MaxValue(600)]
        [Required]
        public int MaxCreditsDurationSeconds { get; set; } = 360;

        [DisplayName("最短剧情起始(秒)")]
        [Description("用于避免把前置剧情误判为片头。")]
        [VisibleCondition(nameof(EnableIntroMarker), SimpleCondition.IsTrue)]
        [MinValue(30)]
        [MaxValue(120)]
        [Required]
        public int MinOpeningPlotDurationSeconds { get; set; } = 60;

        [DisplayName("片头探测最大并发数")]
        [Description("限制 AudioFingerprint 片头探测同时运行的条目数，修改后重启生效，默认 1。调大可加快扫描，但会增加 CPU 和磁盘压力。")]
        [MinValue(1)]
        [MaxValue(10)]
        [Required]
        public int IntroDetectionMaxConcurrentCount { get; set; } = 1;

        [DisplayName("片头指纹分钟数")]
        [Description("范围 2-20，默认 10。将同步到媒体库的 IntroDetectionFingerprintLength。")]
        [MinValue(2)]
        [MaxValue(20)]
        [Required]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;

        [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("打标库范围")]
        [Description("用于播放行为打标，留空表示所有剧集库。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string LibraryScope { get; set; } = string.Empty;

        [DisplayName("用户范围")]
        [Description("允许触发打标的用户 ID，逗号或分号分隔；留空表示所有用户。")]
        public string UserScope { get; set; } = string.Empty;

        public void Initialize() {
            SubsequentMarkerModeList.Clear();
            foreach (SubsequentMarkerMode item in Enum.GetValues(typeof(SubsequentMarkerMode)))
                SubsequentMarkerModeList.Add(new EditorSelectOption {
                    Value = item.ToString(),
                    Name = GetSubsequentMarkerModeDisplayName(item),
                    IsEnabled = true
                });
        }

        public override IEditObjectContainer CreateEditContainer() {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0) return container;

            var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.EditorItems) {
                var key = item.Name ?? item.Id;
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

            AddGroup("扫描片头", "",
                nameof(UnlockIntroSkip),
                nameof(ScanIntroOnFavorite),
                nameof(FilterPlainChapters),
                nameof(IntroDetectionFingerprintMinutes),
                nameof(IntroDetectionMaxConcurrentCount));

            AddGroup("播放行为打标",
                "最短剧情起始前: 优先视为前置剧情保护区；最短剧情起始到最大片头时长: 片头更可信；" +
                "超过最大片头时长: 不再判为片头；距离结束小于最大片尾时长: 可判为片尾。",
                nameof(EnableIntroMarker),
                nameof(IntroMarkerMode),
                nameof(MinOpeningPlotDurationSeconds),
                nameof(MaxIntroDurationSeconds),
                nameof(EnableCreditsMarker),
                nameof(CreditsMarkerMode),
                nameof(MaxCreditsDurationSeconds),
                nameof(LibraryScope),
                nameof(UserScope));

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

        private static string GetSubsequentMarkerModeDisplayName(SubsequentMarkerMode option) {
            return option switch {
                SubsequentMarkerMode.CurrentOnly => "仅设置本集，不作用于后续剧集，Emby 系统生成的标记不会被覆盖",
                SubsequentMarkerMode.FillMissing => "补全缺失，而且会更新插件的标记",
                SubsequentMarkerMode.Overwrite => "覆盖后续插件标记，Emby 系统生成的标记不会被覆盖",
                _ => option.ToString()
            };
        }
    }
}
