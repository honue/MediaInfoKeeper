using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask {
    public class ScanAllIntroTask : IScheduledTask {
        private readonly ILogger logger;

        public ScanAllIntroTask(ILogManager logManager) {
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperScanAllIntroTask";

        public string Name => "09.补齐缺失的片头片尾标记";

        public string Description =>
            "检查所选媒体库，只扫描没有片头标记的剧集；已有标记自动跳过。留空表示全部媒体库。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            logger.Info("补齐缺失的片头片尾标记计划任务开始");

            var scope = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.ScanAllIntro.Libraries;
            var candidates = Plugin.LibraryService.FetchScheduledTaskLibraryItems(scope)
                .OfType<Episode>()
                .ToList();
            var episodes = candidates
                .Where(episode => !IntroScanRunner.HasIntroMarkers(episode))
                .ToList();
            var skipped = candidates.Count - episodes.Count;
            logger.Info($"一键扫描片头片尾候选 {candidates.Count} 项，缺少标记 {episodes.Count} 项");

            if (episodes.Count == 0) {
                progress.Report(100.0);
                logger.Info($"一键扫描片头片尾完成：成功 0，跳过 {skipped}，失败 0");
                return;
            }

            var completed = 0;
            var succeeded = 0;
            var failed = 0;
            var tasks = new List<Task>(episodes.Count);
            foreach (var episode in episodes) {
                cancellationToken.ThrowIfCancellationRequested();
                tasks.Add(ProcessEpisodeAsync(episode));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            progress.Report(100.0);
            logger.Info($"一键扫描片头片尾完成：成功 {succeeded}，跳过 {skipped}，失败 {failed}");

            async Task ProcessEpisodeAsync(Episode episode) {
                try {
                    var result = await IntroScanRunner
                        .ScanEpisodeAsync(episode, "一键扫描片头片尾", cancellationToken,
                            RefreshPriority.Low)
                        .ConfigureAwait(false);
                    if (result && IntroScanRunner.HasIntroMarkers(episode))
                        Interlocked.Increment(ref succeeded);
                    else
                        Interlocked.Increment(ref failed);
                }
                catch (OperationCanceledException) {
                    throw;
                }
                catch (Exception ex) {
                    Interlocked.Increment(ref failed);
                    logger.Error($"一键扫描片头片尾失败: {episode.FileName ?? episode.Path ?? episode.Name}");
                    logger.Error(ex.Message);
                    logger.Debug(ex.StackTrace);
                }
                finally {
                    var done = Interlocked.Increment(ref completed);
                    progress.Report(done / (double)episodes.Count * 100);
                }
            }
        }
    }
}
