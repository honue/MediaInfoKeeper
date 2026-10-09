using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask {
    public class ExportExistingMediaInfoTask : IScheduledTask {
        private readonly ILogger logger;

        public ExportExistingMediaInfoTask(ILogManager logManager) {
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperExportExistingMediaInfoTask";

        public string Name => "03.备份已有媒体信息";

        public string Description => "把所选媒体库中已有的媒体流信息保存为 JSON；没有媒体流信息的条目自动跳过。";

        public string Category => Plugin.TaskCategoryName;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            logger.Info("计划任务执行(仅导出已有 MediaInfo)");

            var items = Plugin.LibraryService.FetchScheduledTaskLibraryItems(
                Plugin.Instance.Options.MainPage.ScheduledTasksEditor.ExportExistingMediaInfo
                    .ExportExistingMediaInfoLibraries,
                includeAudio: true);
            logger.Info($"计划任务条目数 {items.Count}");
            var total = items.Count;
            if (total == 0) {
                progress.Report(100.0);
                logger.Info("计划任务完成(0 个条目)");
                return Task.CompletedTask;
            }

            var current = 0;
            var hasMediaInfo = 0;
            foreach (var item in items) {
                if (cancellationToken.IsCancellationRequested) {
                    logger.Info("计划任务已取消");
                    return Task.CompletedTask;
                }

                if (!Plugin.MediaInfoService.HasMediaInfo(item)) {
                    current++;
                    progress.Report(current / (double)total * 100);
                    continue;
                }

                try {
                    Plugin.MediaSourceInfoStore?.OverWriteToFile(item);
                    if (item is Audio)
                        Plugin.EmbeddedInfoStore?.OverWriteToFile(item);
                    else
                        Plugin.ChaptersStore?.OverWriteToFile(item);

                    hasMediaInfo++;
                }
                catch (OperationCanceledException) {
                    logger.Info($"计划任务已取消 {item.Path}");
                    return Task.CompletedTask;
                }
                catch (Exception e) {
                    logger.Error($"计划任务失败: {item.Path}");
                    logger.Error(e.Message);
                    logger.Debug(e.StackTrace);
                }

                current++;
                progress.Report(current / (double)total * 100);
            }

            logger.Info($"计划任务完成 导出 {hasMediaInfo} 条目");
            return Task.CompletedTask;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            yield return new TaskTriggerInfo {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Thursday,
                TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
            };
        }
    }
}
