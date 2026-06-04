using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ScanRecentIntroTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        public ScanRecentIntroTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }

        public string Key => "MediaInfoKeeperScanRecentIntroTask";

        public string Name => "03.扫描片头";

        public string Description => "按本任务配置的媒体库范围，取最近条目的剧集执行片头检测。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("最近入库片头扫描计划任务开始");
            var episodes = FetchRecentEpisodes();
            await Plugin.IntroScanService
                .ScanEpisodesAsync(episodes, cancellationToken, progress)
                .ConfigureAwait(false);
            this.logger.Info("最近入库片头扫描计划任务完成");
        }

        private List<Episode> FetchRecentEpisodes()
        {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.ScanRecentIntro;
            var limit = taskOptions.ScanRecentIntroLimit;
            var episodes = Plugin.LibraryService.FetchScheduledTaskLibraryItems(
                    taskOptions.ScanRecentIntroLibraries,
                    orderByDateCreatedDesc: true,
                    take: Math.Max(1, limit))
                .OfType<Episode>()
                .ToList();
            this.logger.Info($"扫描条目数 {episodes.Count}");
            return episodes;
        }
    }
}
