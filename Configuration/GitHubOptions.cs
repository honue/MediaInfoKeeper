using System.ComponentModel;
using Emby.Web.GenericEdit;

namespace MediaInfoKeeper.Configuration
{
    public class GitHubOptions : EditableOptionsBase
    {
        public override string EditorTitle => "GitHub";

        [DisplayName("项目地址")]
        public string ProjectUrl { get; set; } = "https://github.com/honue/MediaInfoKeeper";

        [DisplayName("最新版本")]
        [Description("从 GitHub Releases 获取最新版本号。项目初期，有许多不完善的地方，请及时关注更新。")]
        public string LatestReleaseVersion { get; set; } = "加载中";

        [DisplayName("当前版本")]
        public string CurrentVersion { get; set; } = "未知";
    }
}
