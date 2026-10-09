using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask {
    public class SubmitMarkersTask : IScheduledTask {
        private readonly ILogger logger;

        public SubmitMarkersTask(ILogManager logManager) {
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperSubmitTheIntroDbMarkersTask";

        public string Name => "06.共享片头片尾标记";

        public string Description => "把所选媒体库中已有的片头片尾标记提交到已配置的 TheIntroDB 和 IntroDB；未配置 API Key 的服务不会提交。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            logger.Info("片头片尾共享计划任务开始");

            var submitTheIntroDb = !string.IsNullOrWhiteSpace(
                Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.TheIntroDb?.ApiKey);
            var submitIntroDb = !string.IsNullOrWhiteSpace(
                Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.IntroDb?.ApiKey);
            if (!submitTheIntroDb && !submitIntroDb) {
                progress?.Report(100.0);
                logger.Warn("片头片尾共享计划任务跳过: TheIntroDB 和 IntroDB 均未配置 API Key");
                return;
            }

            var items = FetchScopedItems(submitTheIntroDb);
            var total = items.Count;
            if (total == 0) {
                progress?.Report(100.0);
                logger.Info("片头片尾共享计划任务完成: 条目数 0");
                return;
            }

            var processed = 0;
            var targetSucceeded = 0;
            var targetSkipped = 0;
            var targetFailed = 0;
            var submittedSegments = 0;
            var theIntroDbRateLimited = false;

            foreach (var item in items) {
                if (cancellationToken.IsCancellationRequested) {
                    logger.Info("片头片尾共享计划任务已取消");
                    return;
                }

                processed++;
                if (submitTheIntroDb && !theIntroDbRateLimited)
                    try {
                        var result = await TheIntroDbService.SubmitMarkersAsync(item, cancellationToken)
                            .ConfigureAwait(false);
                        submittedSegments += result.SubmittedSegments;
                        if (result.Succeeded) {
                            targetSucceeded++;
                        }
                        else if (result.Skipped) {
                            targetSkipped++;
                        }
                        else {
                            targetFailed++;
                            if (result.RateLimited) {
                                theIntroDbRateLimited = true;
                                logger.Warn("TheIntroDB 上报遇到限流，本轮停止向 TheIntroDB 上报");
                            }
                        }
                    }
                    catch (OperationCanceledException) {
                        logger.Info("片头片尾共享计划任务已取消");
                        return;
                    }
                    catch (Exception ex) {
                        targetFailed++;
                        logger.Error("TheIntroDB 上报计划任务异常: " + TheIntroDbService.FormatItemForLog(item));
                        logger.Error(ex.Message);
                        logger.Debug(ex.StackTrace);
                    }

                if (submitIntroDb && item is Episode episode)
                    try {
                        var result = await IntroDbService.SubmitMarkersAsync(episode, cancellationToken)
                            .ConfigureAwait(false);
                        submittedSegments += result.SubmittedSegments;
                        if (result.Succeeded) {
                            targetSucceeded++;
                        }
                        else if (result.Skipped) {
                            targetSkipped++;
                        }
                        else {
                            targetFailed++;
                        }
                    }
                    catch (OperationCanceledException) {
                        logger.Info("片头片尾共享计划任务已取消");
                        return;
                    }
                    catch (Exception ex) {
                        targetFailed++;
                        logger.Error("IntroDB 上报计划任务异常: " + IntroDbService.FormatItemForLog(episode));
                        logger.Error(ex.Message);
                        logger.Debug(ex.StackTrace);
                    }

                progress?.Report(processed / (double)total * 100);
            }

            progress?.Report(100.0);
            logger.Info(
                "片头片尾共享计划任务完成: total={0}, processed={1}, targetSucceeded={2}, targetSkipped={3}, targetFailed={4}, submittedSegments={5}",
                total,
                processed,
                targetSucceeded,
                targetSkipped,
                targetFailed,
                submittedSegments);
        }

        private List<BaseItem> FetchScopedItems(bool includeMovies) {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.SubmitTheIntroDbMarkers;
            var days = taskOptions.SubmitTheIntroDbMarkersDays;
            var cutoff = days > 0
                ? ConfiguredDateTime.Now.AddDays(-days)
                : (DateTime?)null;
            var items = Plugin.LibraryService.FetchRecentScheduledTaskLibraryItems(
                    cutoff,
                    taskOptions.SubmitTheIntroDbMarkersLibraries)
                .Where(item => item is Episode || (includeMovies && item is Movie))
                .Where(HasAnyMarkers)
                .ToList();

            logger.Info("片头片尾共享候选条目数 {0}, 天数窗口: {1}", items.Count,
                cutoff == null ? "不限制" : days.ToString());
            return items;
        }

        private static bool HasAnyMarkers(BaseItem item) {
            var chapters = Plugin.IntroSkipChapterApi.GetChapters(item);
            return chapters != null && chapters.Any(chapter =>
                chapter != null &&
                (chapter.MarkerType == MarkerType.IntroStart ||
                 chapter.MarkerType == MarkerType.IntroEnd ||
                 chapter.MarkerType == MarkerType.CreditsStart));
        }
    }
}
