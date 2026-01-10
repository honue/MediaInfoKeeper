using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;

namespace MediaInfoKeeper.Configuration
{
    public class GeneralOptions : EditableOptionsBase
    {
        public override string EditorTitle => "全局设置";

        [DisplayName("启用 MediaInfo")]
        [Description("启用后优先从 JSON 恢复，提取后再写入 JSON。")]
        public bool PersistMediaInfoEnabled { get; set; } = true;

        [DisplayName("条目移除时删除 JSON")]
        [Description("启用后，条目移除时删除已持久化的 JSON。")]
        public bool DeleteMediaInfoJsonOnRemove { get; set; } = false;

        [DisplayName("禁用 Emby 系统 ffprobe")]
        [Description("开启后阻止 Emby 自带的 ffprobe 运行，仅插件内部允许调用。")]
        public bool DisableSystemFfprobe { get; set; } = true;

        [DisplayName("禁用 Emby 系统元数据刷新")]
        [Description("开启后阻止 Emby 默认的 CanRefresh 通过，仅插件内部允许调用。")]
        public bool DisableSystemMetadataRefresh { get; set; } = true;

        [DisplayName("MediaInfo JSON 存储根目录")]
        [Description("为空时，JSON 保存到媒体文件同目录。填写后会用这个值，拼接媒体路径存储Json。")]
        [Editor(typeof(EditorFolderPicker), typeof(EditorBase))]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;
    }
}
