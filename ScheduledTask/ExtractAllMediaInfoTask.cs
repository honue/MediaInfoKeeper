using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask {
    public class ExtractAllMediaInfoTask : IScheduledTask {
        private readonly ILogger logger;

        public ExtractAllMediaInfoTask(ILogManager logManager) {
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperExtractAllMediaInfoTask";

        public string Name => "08.补齐缺失的媒体信息";

        public string Description =>
            "检查所选媒体库，只为没有媒体流信息的条目执行媒体探测；已有媒体流、影片元数据和图片均不修改。留空表示全部媒体库。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            logger.Info("补齐缺失的媒体信息计划任务开始");

            var scope = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.ExtractAllMediaInfo.Libraries;
            var candidates = Plugin.LibraryService.FetchScheduledTaskLibraryItems(scope, includeAudio: true);
            var items = new List<BaseItem>();
            foreach (var item in candidates)
                if (!Plugin.MediaInfoService.HasMediaInfo(item))
                    items.Add(item);

            var skipped = candidates.Count - items.Count;
            var total = items.Count;
            logger.Info($"一键提取媒体信息候选 {candidates.Count} 项，缺少媒体流 {total} 项");
            if (total == 0) {
                progress.Report(100.0);
                logger.Info($"一键提取媒体信息完成：成功 0，跳过 {skipped}，失败 0");
                return;
            }

            var completed = 0;
            var succeeded = 0;
            var failed = 0;
            var tasks = new List<Task>(total);
            foreach (var item in items) {
                cancellationToken.ThrowIfCancellationRequested();
                tasks.Add(ProcessItemAsync(item));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            progress.Report(100.0);
            logger.Info($"一键提取媒体信息完成：成功 {succeeded}，跳过 {skipped}，失败 {failed}");

            async Task ProcessItemAsync(BaseItem item) {
                try {
                    var extracted = await MediaInfoRunner
                        .ExtractMediaInfoAsync(item.InternalId, "一键提取媒体信息", cancellationToken,
                            priority: RefreshPriority.Low)
                        .ConfigureAwait(false);
                    if (extracted)
                        Interlocked.Increment(ref succeeded);
                    else
                        Interlocked.Increment(ref failed);
                }
                catch (OperationCanceledException) {
                    throw;
                }
                catch (Exception ex) {
                    Interlocked.Increment(ref failed);
                    var displayName = item.FileName ?? item.Path ?? item.Name;
                    logger.Error($"一键提取媒体信息失败: {displayName}");
                    logger.Error(ex.Message);
                    logger.Debug(ex.StackTrace);
                }
                finally {
                    var done = Interlocked.Increment(ref completed);
                    progress.Report(done / (double)total * 100);
                }
            }
        }
    }
}
