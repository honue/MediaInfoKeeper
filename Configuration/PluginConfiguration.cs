using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "MediaInfoKeeper";

        public override string EditorDescription => "将媒体信息与章节保存为 JSON，并在需要时从 JSON 恢复。";

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("追更媒体库")]
        [Description("用于入库触发与删除 JSON 逻辑；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string CatchupLibraries { get; set; } = string.Empty;

        [Browsable(false)]
        public string ScopedLibraries { get; set; } = string.Empty;

        [DisplayName("启用 MediaInfo")]
        [Description("启用后优先从 JSON 恢复，提取后再写入 JSON。")]
        public bool PersistMediaInfoEnabled { get; set; } = true;

        [DisplayName("条目移除时删除 JSON")]
        [Description("启用后，条目移除时删除已持久化的 JSON。")]
        public bool DeleteMediaInfoJsonOnRemove { get; set; } = false;

        [DisplayName("计划任务媒体库")]
        [Description("用于计划任务范围；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string ScheduledTaskLibraries { get; set; } = string.Empty;

        [DisplayName("最近入库条目数量")]
        [Description("用于“最近条目提取媒体信息”计划任务。默认 100。")]
        [MinValue(1)]
        [MaxValue(1000)]
        public int RecentItemsLimit { get; set; } = 100;

        [DisplayName("MediaInfo JSON 存储根目录")]
        [Description("为空时，JSON 保存到媒体文件同目录。")]
        [Editor(typeof(EditorFolderPicker), typeof(EditorBase))]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;


        [DisplayName("GitHub")]
        public string projectUrl { get; set; } = "https://github.com/honue/MediaInfoKeeper";
    }
}
