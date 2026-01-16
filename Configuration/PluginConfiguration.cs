using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "MediaInfoKeeper";

        public override string EditorDescription => "将媒体信息与章节保存为 JSON，并在需要时从 JSON 恢复。";

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("全局设置")]
        public GeneralOptions General { get; set; } = new GeneralOptions();

        [DisplayName("媒体库范围")]
        public LibraryScopeOptions LibraryScope { get; set; } = new LibraryScopeOptions();

        [DisplayName("计划任务参数")]
        public RecentTaskOptions RecentTasks { get; set; } = new RecentTaskOptions();

        [DisplayName("IntroSkip")]
        public IntroSkipOptions IntroSkip { get; set; } = new IntroSkipOptions();

        [DisplayName("GitHub")]
        public GitHubOptions GitHub { get; set; } = new GitHubOptions();
    }
}
