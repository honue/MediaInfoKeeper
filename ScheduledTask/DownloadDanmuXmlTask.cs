using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Options;

namespace MediaInfoKeeper.ScheduledTask
{
    public class DownloadDanmuXmlTask : IScheduledTask
    {
        private readonly ILogger logger;

        public DownloadDanmuXmlTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperDownloadDanmuXmlTask";

        public string Name => "07.下载弹幕";

        public string Description => "按本任务配置的媒体库范围与最近入库时间窗口，为电影和剧集下载弹幕。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("弹幕下载计划任务开始");

            if (Plugin.DanmuService?.IsEnabled != true)
            {
                this.logger.Info("弹幕下载计划任务跳过: 未配置弹幕 API BaseUrl");
                progress?.Report(100.0);
                return;
            }

            var items = FetchRecentScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                this.logger.Info("弹幕下载计划任务完成: 条目数 0");
                progress?.Report(100.0);
                return;
            }

            var networkFirst = string.Equals(
                Plugin.Instance?.Options?.MetaData?.DanmuFetchMode,
                MetaDataOptions.DanmuFetchModeOption.NetworkFirst.ToString(),
                StringComparison.Ordinal);
            this.logger.Info($"弹幕下载计划任务策略: {(networkFirst ? "网络优先(始终拉取并覆盖本地)" : "本地优先(本地存在则跳过)")}");

            var completed = 0;
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var result = await Plugin.DanmuService
                        .QueueDownloadWithReasonAsync(item.InternalId, networkFirst, cancellationToken)
                        .ConfigureAwait(false);

                    if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.Reason))
                    {
                        this.logger.Info($"弹幕下载: 跳过 {item.FileName} {result.Reason}");
                    }
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info($"弹幕下载: 取消 {item.FileName}");
                }
                catch (Exception ex)
                {
                    this.logger.Info($"弹幕下载: 失败 {item.FileName} {ex.Message}");
                    this.logger.Debug(ex.StackTrace);
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(done / (double)total * 100);
                }
            }

            this.logger.Info($"弹幕下载计划任务完成: 条目数 {total}");
        }

        private List<BaseItem> FetchRecentScopedItems()
        {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.DownloadDanmuXml;
            var taskScope = taskOptions.DownloadDanmuXmlLibraries;
            var days = taskOptions.DownloadDanmuXmlDays;
            var cutoff = days > 0
                ? ConfiguredDateTime.Now.AddDays(-days)
                : (DateTime?)null;
            var items = Plugin.LibraryService.FetchRecentScheduledTaskLibraryItems(taskScopedLibraries: taskScope, cutoff: cutoff, orderByDateCreatedDesc: true)
                .Where(item => item is Episode || item is Movie)
                .ToList();

            this.logger.Info($"弹幕下载计划任务条目数: {items.Count}, 天数窗口: {(cutoff == null ? "不限制" : days.ToString())}");
            return items;
        }
    }
}
