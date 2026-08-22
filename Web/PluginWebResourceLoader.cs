using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Controller.Configuration;

namespace MediaInfoKeeper.Web {
    internal static class PluginWebResourceLoader {
        public static string ModifiedShortcutsString { get; private set; }

        public static string ModifiedRefreshDialogString { get; private set; }

        public static MemoryStream MediaInfoKeeperJs { get; private set; }

        public static MemoryStream EdeJs { get; private set; }

        public static void Initialize(IServerConfigurationManager configurationManager) {
            try {
                PatchHtmlVideoPlayer(configurationManager);
                MediaInfoKeeperJs = GetResourceStream("mediainfokeeper.js");
                EdeJs = GetResourceStream("ede.js");
                BuildShortcutBootstrap(configurationManager);
                BuildRefreshDialogPatch(configurationManager);
            }
            catch (Exception e) {
                Plugin.Instance.Logger.Error($"{nameof(PluginWebResourceLoader)} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        private static MemoryStream GetResourceStream(string resourceName) {
            var name = typeof(Plugin).Namespace + ".Resources." + resourceName;
            var manifestResourceStream =
                typeof(PluginWebResourceLoader).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            var destination = new MemoryStream((int)manifestResourceStream.Length);
            manifestResourceStream.CopyTo(destination);
            destination.Position = 0;
            return destination;
        }

        private static void PatchHtmlVideoPlayer(IServerConfigurationManager configurationManager) {
            var pluginPath = string.Empty;
            try {
                var dashboardSourcePath = configurationManager.Configuration.DashboardSourcePath ??
                                          Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath,
                                              "dashboard-ui");
                pluginPath = Path.Combine(dashboardSourcePath, "modules", "htmlvideoplayer", "plugin.js");
                if (!File.Exists(pluginPath)) return;

                const string source = "&&(elem.crossOrigin=initialSubtitleStream)";
                var content = File.ReadAllText(pluginPath);
                var patchedContent = content.Replace(source, string.Empty);
                if (!string.Equals(content, patchedContent, StringComparison.Ordinal)) File.WriteAllText(pluginPath, patchedContent);
            }
            catch (UnauthorizedAccessException ex) {
                Plugin.Instance.Logger.Error(
                    "PatchHtmlVideoPlayer failed：移除跨域限制失败，无权限修改 dashboard 文件 {0}，请检查 Emby 进程用户和目录挂载权限。", pluginPath);
                Plugin.Instance.Logger.Error(ex.StackTrace);
            }
            catch (Exception ex) {
                Plugin.Instance.Logger.Warn("PatchHtmlVideoPlayer failed: {0}", ex.Message);
                Plugin.Instance.Logger.Error(ex.StackTrace);
            }
        }

        private static void BuildShortcutBootstrap(IServerConfigurationManager configurationManager) {
            var dashboardSourcePath = configurationManager.Configuration.DashboardSourcePath ??
                                      Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath,
                                          "dashboard-ui");

            const string bootstrapScript = @"
;(function loadMediaInfoKeeperShortcutModule() {
    if (window.__mediaInfoKeeperShortcutBootstrapLoaded) {
        return;
    }
    window.__mediaInfoKeeperShortcutBootstrapLoaded = true;

    if (typeof require !== 'function') {
        return;
    }

    require(['components/mediainfokeeper/mediainfokeeper'], function () {}, function () {});
})();
";

            ModifiedShortcutsString = File.ReadAllText(Path.Combine(dashboardSourcePath, "modules", "shortcuts.js")) +
                                      bootstrapScript;
        }

        private static void BuildRefreshDialogPatch(IServerConfigurationManager configurationManager) {
            var dashboardSourcePath = configurationManager.Configuration.DashboardSourcePath ??
                                      Path.Combine(configurationManager.ApplicationPaths.ApplicationResourcesPath,
                                          "dashboard-ui");

            var refreshDialogPath = Path.Combine(dashboardSourcePath, "modules", "refreshdialog", "refreshdialog.js");
            if (!File.Exists(refreshDialogPath)) {
                Plugin.Instance.Logger.Warn("刷新元数据弹窗注入已跳过：未找到源文件 {0}", refreshDialogPath);
                ModifiedRefreshDialogString = string.Empty;
                return;
            }

            var source = File.ReadAllText(refreshDialogPath);
            var patched = source;

            patched = patched.Replace(
                "replaceThumbnailImages=dlg.querySelector(\".chkReplaceThumbnailImages\").checked,options=options.items;return _connectionmanager.default.getApiClient(options[0]).refreshItems(options,{Recursive:!0,ImageRefreshMode:mode,MetadataRefreshMode:mode,ReplaceAllImages:replaceAllImages,ReplaceThumbnailImages:replaceThumbnailImages,ReplaceAllMetadata:replaceAllMetadata})",
                "replaceThumbnailImages=dlg.querySelector(\".chkReplaceThumbnailImages\").checked,allowFfProcess=dlg.querySelector(\".chkAllowFfProcess\").checked,options=options.items;return _connectionmanager.default.getApiClient(options[0]).refreshItems(options,{Recursive:!0,ImageRefreshMode:mode,MetadataRefreshMode:mode,ReplaceAllImages:replaceAllImages,ReplaceThumbnailImages:replaceThumbnailImages,ReplaceAllMetadata:replaceAllMetadata,AllowFfProcess:allowFfProcess})");

            patched = patched.Replace(
                "+\"</div>\"+\"<br />\"+'<div class=\"formDialogFooter\">'",
                "+\"</div>\"+'<div class=\"toggleContainer fldAllowFfProcess\">'+\"<label>\"+'<input type=\"checkbox\" is=\"emby-toggle\" class=\"chkAllowFfProcess\" />'+'<span>允许使用 ffprocess</span>'+\"</label>\"+'<div class=\"toggleFieldDescription fieldDescription\">Strm 需要截图或提取内嵌信息时，允许执行 ffprocess。</div>'+\"</div>\"+\"<br />\"+'<div class=\"formDialogFooter\">'");

            if (string.Equals(source, patched, StringComparison.Ordinal)) Plugin.Instance.Logger.Warn("刷新元数据弹窗注入已跳过：未找到预期注入锚点");

            ModifiedRefreshDialogString = patched;
        }
    }
}
