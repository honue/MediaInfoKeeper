using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;

namespace MediaInfoKeeper.Configuration
{
    public class GeneralOptions : EditableOptionsBase
    {
        public override string EditorTitle => "全局设置";

        [DisplayName("启用 MediaInfoKeeper 插件")]
        [Description("启用后优先从 JSON 恢复，提取后再写入 JSON。")]
        public bool PersistMediaInfoEnabled { get; set; } = true;

        [DisplayName("条目移除时删除 JSON")]
        [Description("启用后，条目移除时删除已持久化的 JSON。")]
        public bool DeleteMediaInfoJsonOnRemove { get; set; } = false;

        [DisplayName("禁用 Emby 系统 ffprobe")]
        [Description("开启后阻止 Emby 自带 ffprobe 运行，仅插件内部允许调用。")]
        public bool DisableSystemFfprobe { get; set; } = true;

        [DisplayName("启用剧集元数据变动监听")]
        [Description("开启后将监控媒体元数据刷新过程，当剧集触发封面刷新时延迟恢复媒体信息，避免 .strm 刷新后媒体信息丢失。")]
        public bool EnableMetadataProvidersWatcher { get; set; } = true;

        [DisplayName("MediaInfo JSON 存储根目录")]
        [Description("为空时，JSON 保存到媒体文件同目录。填写后会用该路径拼接媒体路径存储 JSON。")]
        [Editor(typeof(EditorFolderPicker), typeof(EditorBase))]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;

        [DisplayName("扫描最多并发数")]
        [Description("设置媒体信息多线程提取的最大并发数，默认 2。")]
        [MinValue(1), MaxValue(20)]
        public int MaxConcurrentCount { get; set; } = 2;

    }
}
