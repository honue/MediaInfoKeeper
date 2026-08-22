using System.ComponentModel;
using System.IO;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Options {
    public class MediaInfoOptions : EditableOptionsBase {
        public override string EditorTitle => "媒体信息";

        public override string EditorDescription => string.Empty;

        [DisplayName("启用 MediaInfo 预加载")]
        [Description("播放剧集时，预加载下一集媒体信息；关闭后不再自动预加载。")]
        public bool EnableMediaInfoPrefetch { get; set; } = true;

        [DisplayName("浏览剧集提取媒体信息")]
        [Description("浏览视频或音频详情接口时，若条目没有媒体信息，则后台提取并写入 JSON。")]
        public bool ExtractMediaInfoOnItemDetail { get; set; } = false;

        [DisplayName("条目移除时删除 JSON")]
        [Description("启用后，条目移除时删除已持久化的 JSON。")]
        public bool DeleteMediaInfoJsonOnRemove { get; set; } = false;

        [DisplayName("MediaInfo JSON 存储路径模板")]
        [Description("默认保存到 Emby 的 /config/data/MediaInfoKeeper。留空时保存到媒体文件同目录。支持路径模板，详见 https://github.com/honue/MediaInfoKeeper/wiki")]
        [EditFolderPicker]
        public string MediaInfoJsonRootFolder { get; set; } = GetDefaultMediaInfoJsonRootFolder();

        [DisplayName("提取尝试次数")]
        [Description("媒体信息刷新后仍检测不到音频或视频流时的最大尝试次数，包含首次提取。")]
        [MinValue(1)]
        [MaxValue(10)]
        public int ExtractMediaInfoAttemptCount { get; set; } = 3;

        [DisplayName("提取任务并发数")]
        [Description("设置媒体信息提取的最大并发数，修改后重启生效，默认 1。")]
        [MinValue(1)]
        [MaxValue(20)]
        public int MaxConcurrentCount { get; set; } = 1;

        public void Initialize() {
        }

        public override IEditObjectContainer CreateEditContainer() {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0) return container;

            root.EditorItems = new EditorBase[] {
                new EditorGroup("媒体信息", root.EditorItems, "group1", root.Id, null) {
                    Description = "插件会持续监听 .strm 文件内容变更，并阻止 Emby 系统 ffprobe/ffmpeg 运行；仅在插件内部需要提取媒体信息时按需放行。"
                }
            };

            return container;
        }

        internal static string GetDefaultMediaInfoJsonRootFolder() {
            try {
                var programDataPath = Plugin.Instance?.AppHost?.Resolve<IApplicationPaths>()?.ProgramDataPath;
                if (!string.IsNullOrWhiteSpace(programDataPath)) return Path.Combine(programDataPath, "data", Plugin.PluginName);
            }
            catch {
            }

            return Path.Combine("/config", "data", Plugin.PluginName);
        }
    }
}
