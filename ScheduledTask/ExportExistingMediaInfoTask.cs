using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Store;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ExportExistingMediaInfoTask : IScheduledTask
    {
        private readonly ILogger logger;

        public ExportExistingMediaInfoTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }
        public string Key => "MediaInfoKeeperExportExistingMediaInfoTask";

        public string Name => "05.备份媒体信息";

        public string Description => "按本任务配置的媒体库范围，将已存在 MediaInfo 的条目导出为 JSON，无 MediaInfo 则跳过。";

        public string Category => Plugin.TaskCategoryName;

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("计划任务执行(仅导出已有 MediaInfo)");

            var items = FetchScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成(0 个条目)");
                return Task.CompletedTask;
            }

            var current = 0;
            var hasMediaInfo = 0;
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("计划任务已取消");
                    return Task.CompletedTask;
                }

                if (!Plugin.MediaInfoService.HasMediaInfo(item))
                {
                    current++;
                    progress.Report(current / (double)total * 100);
                    continue;
                }

                try
                {
                    if (Plugin.MediaInfoService.HasMediaInfo(item))
                    {
                        MediaInfoPersist.OverWritePersistedMedia(item);
                        hasMediaInfo++;
                    }
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info($"计划任务已取消 {item.Path}");
                    return Task.CompletedTask;
                }
                catch (Exception e)
                {
                    this.logger.Error($"计划任务失败: {item.Path}");
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }

                current++;
                progress.Report(current / (double)total * 100);
            }

            this.logger.Info($"计划任务完成 导出 {hasMediaInfo} 条目");
            return Task.CompletedTask;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerWeekly,
                DayOfWeek = DayOfWeek.Thursday,
                TimeOfDayTicks = TimeSpan.FromHours(1).Ticks
            };
        }

        private List<BaseItem> FetchScopedItems()
        {
            var items = Plugin.LibraryService.FetchScheduledTaskLibraryItems(
                Plugin.Instance.Options.MainPage.ScheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries,
                includeAudio: true);
            this.logger.Info($"计划任务条目数 {items.Count}");
            return items;
        }
    }
}
