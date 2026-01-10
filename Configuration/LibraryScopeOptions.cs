using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class LibraryScopeOptions : EditableOptionsBase
    {
        public override string EditorTitle => "媒体库范围";

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("追更媒体库")]
        [Description("用于入库触发与删除 JSON 逻辑；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string CatchupLibraries { get; set; } = string.Empty;

        [DisplayName("计划任务媒体库")]
        [Description("用于计划任务范围；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string ScheduledTaskLibraries { get; set; } = string.Empty;
    }
}
