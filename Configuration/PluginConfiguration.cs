using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;

namespace MediaInfoKeeper.Configuration
{
    public class PluginConfiguration : EditableOptionsBase
    {
        public override string EditorTitle => "MediaInfoKeeper";

        public override string EditorDescription =>
            "将媒体信息与章节保存为 JSON，并在需要时从 JSON 恢复。";

        [DisplayName("启用 MediaInfo")]
        [Description("启用后优先从 JSON 恢复，提取后再写入 JSON。")]
        public bool PersistMediaInfoEnabled { get; set; } = true;

        [DisplayName("条目移除时删除 JSON")]
        [Description("启用后，条目移除时删除已持久化的 JSON。")]
        public bool DeleteMediaInfoJsonOnRemove { get; set; } = false;

        [DisplayName("MediaInfo JSON 存储根目录")]
        [Description("为空时，JSON 保存到媒体文件同目录。")]
        [Editor(typeof(EditorFolderPicker), typeof(EditorBase))]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;
    }
}
