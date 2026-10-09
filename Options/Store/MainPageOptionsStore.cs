using System.Collections.Generic;
using Emby.Web.GenericEdit.Elements;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Options.Store {
    internal class MainPageOptionsStore {
        private readonly PluginOptionsStore pluginOptionsStore;

        public MainPageOptionsStore(PluginOptionsStore pluginOptionsStore) {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public MainPageOptions GetOptions() {
            var options = pluginOptionsStore.GetOptionsForUi();
            var mainPage = options.MainPage ?? new MainPageOptions();
            mainPage.ScheduledTasksEditor ??= new MainPageOptions.ScheduledTaskEditorOptions();
            mainPage.ShowRefreshQueueStatus = options.Enhance?.TakeOverRefreshQueue ?? true;
            mainPage.RefreshQueueStatus = BuildRefreshQueueStatus();
            return mainPage;
        }

        public void SetOptions(MainPageOptions options) {
            var pluginOptions = pluginOptionsStore.GetOptions();
            pluginOptions.MainPage = options ?? new MainPageOptions();
            pluginOptionsStore.SetOptions(pluginOptions);
        }

        internal static StatusItem BuildRefreshQueueStatus() {
            var metadataStats = MetaDataRunner.GetQueueStats();
            var mediaInfoStats = MediaInfoRunner.GetQueueStats();
            var introStats = IntroScanRunner.GetQueueStats();
            var running = new List<string>();
            var waiting = mediaInfoStats.Waiting + metadataStats.Waiting + introStats.Waiting;

            if (mediaInfoStats.Running > 0) running.Add($"提取媒体信息 {mediaInfoStats.Running} 项");
            if (metadataStats.Running > 0) running.Add($"刷新元数据 {metadataStats.Running} 项");
            if (introStats.Running > 0) running.Add($"扫描片头片尾 {introStats.Running} 项");

            var total = mediaInfoStats.Total + metadataStats.Total + introStats.Total;
            var text = total == 0
                ? "当前没有媒体信息、元数据或片头片尾任务"
                : (running.Count > 0 ? "正在" + string.Join("、", running) : "任务正在等待处理") +
                  (waiting > 0 ? $"；另有 {waiting} 项等待" : string.Empty);

            return new StatusItem(
                total == 0 ? "插件处理状态" : "插件正在处理",
                text,
                total > 0 ? ItemStatus.InProgress : ItemStatus.Succeeded);
        }
    }
}
