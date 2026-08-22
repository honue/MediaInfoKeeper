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
using MediaInfoKeeper.Provider;

namespace MediaInfoKeeper.Options {
    public class MetaDataOptions : EditableOptionsBase {
        private static readonly string[] SupportedFallbackLanguages = {
            "zh-SG",
            "zh-HK",
            "zh-TW"
        };

        private static readonly string[] SupportedTvdbFallbackLanguages = {
            "zho",
            "zhtw",
            "yue"
        };

        public override string EditorTitle => "元数据";

        public override string EditorDescription => string.Empty;

        public GenericItemList ScraperEntries { get; set; } = new();

        [Browsable(false)] public ScraperEditorOptions ScrapersEditor { get; set; } = new();

        [DisplayName("刷新元数据并发数")]
        [Description("设置插件刷新元数据任务的最大并发数，修改后重启生效，默认 3。")]
        [MinValue(1)]
        [MaxValue(20)]
        public int MaxConcurrentCount { get; set; } = 3;

        [DisplayName("屏蔽非备选语言简介")]
        [Description("开启后，TMDB/TVDB 的电影/剧集/季/集简介若不在备选语言范围（如英文）将被置空。")]
        public bool BlockNonFallbackLanguage { get; set; } = false;

        [DisplayName("启用 TMDB 中文回退")]
        [Description("按备选语言顺序补全 TMDB 电影/剧集/季/集元数据，并尽量把英文放到最后。")]
        public bool EnableAlternativeTitleFallback { get; set; } = true;

        [Browsable(false)] public List<EditorSelectOption> FallbackLanguageList { get; set; } = new();

        [DisplayName("TMDB 备选语言")]
        [Description("按从左到右优先级回退；会在英文前插入。默认 zh-SG。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(FallbackLanguageList))]
        public string FallbackLanguages { get; set; } = "zh-sg";

        [DisplayName("优先原语言海报")]
        [Description("开启后优先 TMDB 原语言图片结果。")]
        public bool EnableOriginalPoster { get; set; } = false;

        [DisplayName("启用 TMDB 剧集组刮削")]
        [Description("开启后支持按 TMDB 剧集组映射刮削剧集元数据（需在剧集外部ID中填写 TmdbEg，或启用本地剧集组文件）。")]
        public bool EnableMovieDbEpisodeGroup { get; set; } = true;

        [DisplayName("启用缺失剧集增强")]
        [Description("开启后让 Emby 的“查看缺少的集”优先使用 TMDB，并支持按 TMDB 剧集组映射结果展示缺失剧集。")]
        public bool EnableMissingEpisodesEnhance { get; set; } = true;

        [DisplayName("启用本地剧集组文件")]
        [Description("开启后在剧集目录读取 episodegroup.json；当在线剧集组可用时会自动写入本地文件用于后续复用。")]
        public bool EnableLocalEpisodeGroup { get; set; } = false;

        [DisplayName("启用 TVDB 中文回退")]
        [Description("按备选语言顺序补全 TVDB 电影/剧集/季/集元数据，并尽量把英文放到最后。")]
        public bool EnableTvdbFallback { get; set; } = true;

        [Browsable(false)] public List<EditorSelectOption> TvdbFallbackLanguageList { get; set; } = new();

        [DisplayName("TVDB 备选语言")]
        [Description("按从左到右优先级回退；会在英文前插入。默认 zhtw,yue。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(TvdbFallbackLanguageList))]
        public string TvdbFallbackLanguages { get; set; } = "zhtw,yue";

        public void Initialize() {
            EnsureScraperEditors();
            ScraperEntries = new GenericItemList(new[] {
                CreateScraperEntry(IntroDbProvider.ProviderName, "适用条目：集；刮削片头片尾标记。", "metadata.scraper.introDb"),
                CreateScraperEntry(TheIntroDbProvider.ProviderName, "适用条目：电影、集；刮削片头片尾标记。", "metadata.scraper.theIntroDb"),
                CreateScraperEntry(DanmuProvider.ProviderName, "适用条目：电影、集；刮削弹幕。", "metadata.scraper.danmu"),
                CreateScraperEntry(DoubanRoleProvider.ProviderName, "适用条目：电影、剧集、集；使用豆瓣人物角色名。")
            });

            FallbackLanguageList.Clear();
            foreach (var language in SupportedFallbackLanguages)
                FallbackLanguageList.Add(new EditorSelectOption {
                    Value = language.ToLowerInvariant(),
                    Name = language,
                    IsEnabled = true
                });

            TvdbFallbackLanguageList.Clear();
            foreach (var language in SupportedTvdbFallbackLanguages)
                TvdbFallbackLanguageList.Add(new EditorSelectOption {
                    Value = language,
                    Name = language,
                    IsEnabled = true
                });
        }

        public void EnsureScraperEditors() {
            ScrapersEditor ??= new ScraperEditorOptions();
            ScrapersEditor.TheIntroDb ??= new TheIntroDbEditorOptions();
            ScrapersEditor.IntroDb ??= new IntroDbEditorOptions();
            ScrapersEditor.Danmu ??= new DanmuEditorOptions();
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

            AddGroup("元数据", "",
                nameof(MaxConcurrentCount),
                nameof(BlockNonFallbackLanguage)
            );

            AddGroup("刮削器", "配置插件的元数据 Provider 参数。是否启用及优先顺序请在媒体库的元数据 Provider 设置中调整。",
                nameof(ScraperEntries));

            AddGroup("TMDB", "",
                nameof(EnableAlternativeTitleFallback),
                nameof(FallbackLanguages),
                nameof(EnableOriginalPoster),
                nameof(EnableMovieDbEpisodeGroup),
                nameof(EnableMissingEpisodesEnhance),
                nameof(EnableLocalEpisodeGroup));

            AddGroup("TVDB", "",
                nameof(EnableTvdbFallback),
                nameof(TvdbFallbackLanguages));

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
                groupedItems.Add(new EditorGroup("未分组", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            if (groupedItems.Count > 0) root.EditorItems = groupedItems.ToArray();

            return container;
        }

        private static GenericListItem CreateScraperEntry(string name, string description, string commandId = null) {
            var item = new GenericListItem {
                PrimaryText = name,
                SecondaryText = description
            };
            if (!string.IsNullOrEmpty(commandId))
                item.Button2 = new ButtonItem("配置") {
                    CommandId = commandId,
                    Icon = IconNames.settings
                };

            return item;
        }

        public class DanmuEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [DisplayName("加载 dd-danmaku 弹幕js")]
            [Description("修改 index.html，注入 ede.js")]
            public bool EnableDanmakuJs { get; set; } = true;

            [DisplayName("启用弹幕 API")]
            [Description("开启后启用 /api/danmu/{ItemId}/raw 路由的弹幕能力；Senplayer，FileBar、弹幕js等可以直接使Emby提供的弹幕文件，关闭后该路由不提供弹幕返回。")]
            public bool EnableDanmuApi { get; set; } = true;

            [DisplayName("danmu_api BaseUrl")]
            [Description("例如 http://192.168.33.100:9321/token ，danmu_api 项目 https://github.com/huangxd-/danmu_api")]
            [VisibleCondition(nameof(EnableDanmuApi), SimpleCondition.IsTrue)]
            public string DanmuApiBaseUrl { get; set; } = string.Empty;

            [DisplayName("预加载弹幕")]
            [Description("播放剧集时，预加载下一集弹幕到本地。")]
            [VisibleCondition(nameof(EnableDanmuApi), SimpleCondition.IsTrue)]
            public bool EnableDanmuPrefetch { get; set; } = true;

            [DisplayName("始终获取最新弹幕")]
            [Description("开启后请求弹幕时会优先从 danmu_api 拉取最新 xml 并写入本地；拉取完成或超时后返回可用 xml。")]
            [VisibleCondition(nameof(EnableDanmuApi), SimpleCondition.IsTrue)]
            public bool AlwaysFetchLatestDanmu { get; set; } = true;
        }

        public class TheIntroDbEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [DisplayName("API 地址")]
            [Description("TheIntroDB v3 API 地址。是否启用 TheIntroDB Provider 请在媒体库的元数据抓取器中控制。")]
            public string BaseUrl { get; set; } = "https://api.theintrodb.org/v3";

            [DisplayName("API Key")]
            [Description("可选。填写后可提高 TheIntroDB 每日请求额度。共享必填。")]
            public string ApiKey { get; set; } = string.Empty;
        }

        public class IntroDbEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [DisplayName("API 地址")]
            [Description("IntroDB API 地址。是否启用 IntroDB Provider 请在电视剧媒体库的元数据抓取器中控制。")]
            public string BaseUrl { get; set; } = "https://api.introdb.app";

            [DisplayName("API Key")]
            [Description("查询无需填写；共享片头片尾到 IntroDB 时必填。")]
            public string ApiKey { get; set; } = string.Empty;
        }

        public class ScraperEditorOptions : EditableOptionsBase {
            public override string EditorTitle => string.Empty;

            [DisplayName("TheIntroDB")]
            public TheIntroDbEditorOptions TheIntroDb { get; set; } = new();

            [DisplayName("IntroDB")]
            public IntroDbEditorOptions IntroDb { get; set; } = new();

            [DisplayName("Danmu")]
            public DanmuEditorOptions Danmu { get; set; } = new();
        }
    }
}
