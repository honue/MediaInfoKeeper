using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class IntroSkipOptions : EditableOptionsBase
    {
        public override string EditorTitle => "IntroSkip";

        [DisplayName("启用 Strm 片头检测解锁")]
        [Description("开启后允许 .strm 参与 Emby 原生片头指纹探测。")]
        public bool UnlockIntroSkip { get; set; } = true;

        [DisplayName("保护片头标记")]
        [Description("刷新元数据时保护已存在的片头/片尾标记不被清空。")]
        public bool ProtectIntroMarkers { get; set; } = true;

        [DisplayName("启用播放行为打标")]
        [Description("根据播放行为自动标记片头/片尾。")]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayName("最大片头时长(秒)")]
        [Description("超过此时间不再认为是片头区间。")]
        [MinValue(10), MaxValue(600)]
        [Required]
        public int MaxIntroDurationSeconds { get; set; } = 150;

        [DisplayName("最大片尾时长(秒)")]
        [Description("距结尾小于该时长时可标记片尾。")]
        [MinValue(10), MaxValue(600)]
        [Required]
        public int MaxCreditsDurationSeconds { get; set; } = 360;

        [DisplayName("最短剧情起始(秒)")]
        [Description("用于避免把前置剧情误判为片头。")]
        [MinValue(30), MaxValue(120)]
        [Required]
        public int MinOpeningPlotDurationSeconds { get; set; } = 60;

        [DisplayName("片头指纹分钟数")]
        [Description("范围 2-20，默认 10。将同步到媒体库的 IntroDetectionFingerprintLength。")]
        [MinValue(2), MaxValue(20)]
        [Required]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("片头检测库范围")]
        [Description("留空表示所有开启片头检测的剧集库。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string MarkerEnabledLibraryScope { get; set; } = string.Empty;

        [DisplayName("打标库范围")]
        [Description("用于播放行为打标，留空表示所有剧集库。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string LibraryScope { get; set; } = string.Empty;

        [DisplayName("用户范围")]
        [Description("允许触发打标的用户 ID，逗号或分号分隔；留空表示所有用户。")]
        public string UserScope { get; set; } = string.Empty;

    }
}
