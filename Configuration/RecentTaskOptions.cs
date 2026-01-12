using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class RecentTaskOptions : EditableOptionsBase
    {
        public override string EditorTitle => "最近条目计划任务";

        [DisplayName("最近入库媒体筛选数量")]
        [Description("用于“提取媒体信息（最近入库）”计划任务，默认 100。")]
        [MinValue(1)]
        [MaxValue(1000)]
        public int RecentItemsLimit { get; set; } = 100;

        [DisplayName("最近入库时间窗口（天）")]
        [Description("用于“刷新媒体元数据（最近入库）”计划任务，仅处理指定天数内入库的条目，0 表示不限制。")]
        [MinValue(0)]
        [MaxValue(3650)]
        public int RecentItemsDays { get; set; } = 3;

        [Browsable(false)]
        public List<EditorSelectOption> RefreshMetadataModeOptions { get; set; } = new List<EditorSelectOption>
        {
            new EditorSelectOption { Value = "Fill", Name = "补全缺失" },
            new EditorSelectOption { Value = "Replace", Name = "全部替换" }
        };

        [DisplayName("元数据刷新模式")]
        [Description("单选，选择“补全缺失”或“全部替换”元数据/图片。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(RefreshMetadataModeOptions))]
        public string RefreshMetadataMode { get; set; } = "Fill";
    }
}
