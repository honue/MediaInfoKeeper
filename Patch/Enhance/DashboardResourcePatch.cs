using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     拦截仪表盘资源请求，按配置重写首页内容。
    /// </summary>
    public static class DashboardResourcePatch {
        private static Harmony harmony;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool enableDanmakuJs;
        private static MethodInfo modifyHtmlMethod;
        private static bool isPatched;

        private const string BootstrapMarker = "mediainfokeeper-bootstrap";

        // 等 Emby 网页完全启动（ApiClient 出现）后再加载右键菜单脚本。
        // 太早加载会找不到 connectionManager、toast 等模块名。
        private const string BootstrapScript = @"<script id=""mediainfokeeper-bootstrap"">
(function () {
    function start() {
        if (typeof require !== 'function' || !window.ApiClient) {
            setTimeout(start, 300);
            return;
        }
        require(['components/mediainfokeeper/mediainfokeeper'], function () {}, function (err) {
            console.error('[MediaInfoKeeper] 右键菜单脚本加载失败', err);
        });
    }
    start();
})();
</script>";

        public static bool IsReady => harmony != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool pluginEnabled, bool enableDanmaku) {
            if (harmony != null) {
                Configure(pluginEnabled, enableDanmaku);
                return;
            }

            logger = pluginLogger;
            enableDanmakuJs = enableDanmaku;
            isEnabled = pluginEnabled;

            try {
                var embyWebAssembly = Assembly.Load("Emby.Web");
                var embyWebVersion = embyWebAssembly?.GetName().Version;
                var packageCreatorType = embyWebAssembly?.GetType("Emby.Web.Api.PackageCreator", false);

                if (packageCreatorType == null) {
                    PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), "PackageCreator 类型缺失");
                    return;
                }

                modifyHtmlMethod = PatchMethodResolver.Resolve(
                    packageCreatorType,
                    embyWebVersion,
                    new MethodSignatureProfile {
                        Name = "packagecreator-modifyhtml-exact",
                        MethodName = "ModifyHtml",
                        BindingFlags = BindingFlags.Public | BindingFlags.Instance,
                        IsStatic = false,
                        ParameterTypes = new[] {
                            typeof(string),
                            typeof(Stream),
                            typeof(string),
                            typeof(string),
                            typeof(long)
                        },
                        ReturnType = typeof(Task<StreamHandler>)
                    },
                    logger,
                    "DashboardResourcePatch.ModifyHtml");

                if (modifyHtmlMethod == null) {
                    PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), "DashboardResourcePatch 目标方法缺失");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.dashboardresource");
                if (isEnabled) Patch();
            }
            catch (Exception ex) {
                PatchLog.InitFailed(logger, nameof(DashboardResourcePatch), ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                modifyHtmlMethod = null;
                isPatched = false;
            }
        }

        public static void Configure(bool pluginEnabled, bool enableDanmaku) {
            enableDanmakuJs = enableDanmaku;
            isEnabled = pluginEnabled;

            if (harmony == null) return;

            if (isEnabled)
                Patch();
            else
                Unpatch();
        }

        private static void Patch() {
            if (isPatched || harmony == null || modifyHtmlMethod == null) return;

            harmony.Patch(
                modifyHtmlMethod,
                postfix: new HarmonyMethod(typeof(DashboardResourcePatch), nameof(ModifyHtmlPostfix)));
            PatchLog.Patched(logger, nameof(DashboardResourcePatch), modifyHtmlMethod);

            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null) return;

            harmony.UnpatchAll(harmony.Id);
            isPatched = false;
        }

        [HarmonyPostfix]
        private static void ModifyHtmlPostfix([HarmonyArgument(0)] string path, ref Task<StreamHandler> __result) {
            if (!isEnabled) return;

            if (!string.Equals(path, "index.html", StringComparison.OrdinalIgnoreCase) || __result == null) return;

            logger?.Debug("DashboardResourcePatch target hit: {0}", path);
            logger?.Debug("DashboardResourcePatch: 捕获 dashboard 资源请求 path={0}", path);
            __result = RewriteModifyHtmlTaskAsync(__result, path);
        }

        private static async Task<StreamHandler> RewriteModifyHtmlTaskAsync(Task<StreamHandler> originalTask,
            string path) {
            var handler = await originalTask.ConfigureAwait(false);
            if (handler?.Stream == null) {
                logger?.Warn("DashboardResourcePatch: ModifyHtml 返回空流 path={0}", path);
                return handler;
            }

            using var reader = new StreamReader(handler.Stream, Encoding.UTF8, true, 4096, false);
            var originalContent = await reader.ReadToEndAsync().ConfigureAwait(false);
            var rewrittenContent = RewriteDashboardIndexHtml(originalContent);
            var rewrittenBytes = Encoding.UTF8.GetBytes(rewrittenContent);

            handler.Stream = new MemoryStream(rewrittenBytes, false);
            handler.Length = rewrittenBytes.LongLength;
            handler.TotalLength = rewrittenBytes.LongLength;
            return handler;
        }

        private static string RewriteDashboardIndexHtml(string content) {
            if (string.IsNullOrEmpty(content)) {
                logger?.Warn("DashboardResourcePatch: index.html 内容为空，跳过注入");
                return content;
            }

            var bodyCloseIndex = content.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyCloseIndex < 0) {
                logger?.Warn("DashboardResourcePatch: index.html 未找到 </body>，无法注入脚本");
                return content;
            }

            var injection = new StringBuilder();
            if (!content.Contains(BootstrapMarker, StringComparison.OrdinalIgnoreCase)) {
                injection.AppendLine(BootstrapScript);
            }

            const string edeTag = "<script src=\"components/mediainfokeeper/ede.js\" charset=\"utf-8\"></script>";
            if (enableDanmakuJs && !content.Contains(edeTag, StringComparison.OrdinalIgnoreCase)) {
                injection.AppendLine(edeTag);
            }

            if (injection.Length == 0) {
                return content;
            }

            logger?.Debug("DashboardResourcePatch: index.html 注入右键菜单启动脚本{0}成功",
                enableDanmakuJs ? "和弹幕 ede.js " : string.Empty);
            return content.Insert(bodyCloseIndex, injection.ToString());
        }
    }
}
