using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Services;
using static MediaInfoKeeper.Options.MainPageOptions;

namespace MediaInfoKeeper.ScheduledTask {
    public class RefreshRecentMetadataTask : IScheduledTask {
        private readonly ILogger logger;

        public RefreshRecentMetadataTask(ILogManager logManager) {
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperRefreshRecentMetadataTask";

        public string Name => "02.刷新最近入库的元数据";

        public string Description => "按所选媒体库和入库时间筛选条目，再按配置补全或覆盖元数据；完成后从 JSON 恢复已备份的媒体流信息。";

        public string Category => Plugin.TaskCategoryName;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            logger.Info("最近条目刷新元数据计划任务开始");

            var items = FetchRecentItems();
            var total = items.Count;
            if (total == 0) {
                progress.Report(100.0);
                logger.Info("计划任务完成，条目数 0");
                return;
            }

            var replaceMetadata = ShouldReplaceMetadata();
            var replaceImages = ShouldReplaceImages();
            var replaceThumbnails = ShouldReplaceThumbnails();
            var allowFfProcess = ShouldAllowFfProcess();
            var completedSeriesTargets = new List<SeriesRefreshTarget>();
            if (Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata
                .RefreshCompletedSeriesEpisodes)
                completedSeriesTargets = CollectSeriesRefreshTargets(items);

            var totalWork = total + completedSeriesTargets.Count;
            logger.Info(
                $"计划任务条目数{total}，元数据覆盖{replaceMetadata}，图片覆盖{replaceImages}，视频缩略图覆盖{replaceThumbnails}，允许 ffprocess{allowFfProcess}，完整刷新已完结剧集{completedSeriesTargets.Count > 0}");

            var submitted = 0;
            foreach (var item in items) {
                if (cancellationToken.IsCancellationRequested) {
                    logger.Info($"计划任务已取消 {item.Path}");
                    break;
                }

                var options = BuildRefreshOptions(replaceMetadata, replaceImages, replaceThumbnails);
                _ = MetaDataRunner.RefreshMetaDataAsync(item.InternalId, options, CancellationToken.None,
                    RefreshPriority.High, allowFfProcess: allowFfProcess);
                ReportProgress(totalWork, progress, ++submitted);
            }

            foreach (var target in completedSeriesTargets) {
                if (cancellationToken.IsCancellationRequested) {
                    logger.Info($"计划任务已取消完结剧集检查 itemid={target.ItemId}");
                    break;
                }

                var seriesOptions = BuildRefreshOptions(true, replaceImages, replaceThumbnails);
                seriesOptions.Recursive = false;
                var previousStatus = (Plugin.LibraryManager?.GetItemById(target.ItemId) as Series)?.Status;

                await MetaDataRunner.RefreshMetaDataAsync(
                        target.ItemId,
                        seriesOptions,
                        cancellationToken,
                        RefreshPriority.High,
                        true,
                        allowFfProcess)
                    .ConfigureAwait(false);
                ReportProgress(totalWork, progress, ++submitted);

                if (!(Plugin.LibraryManager?.GetItemById(target.ItemId) is Series refreshedSeries)) {
                    logger.Info($"计划任务跳过完结剧集展开：未找到 Series itemid={target.ItemId}");
                    continue;
                }

                if (previousStatus == refreshedSeries.Status || refreshedSeries.Status != SeriesStatus.Ended) continue;

                var episodes = Plugin.LibraryService?.FetchSeriesEpisodes(refreshedSeries) ?? Array.Empty<Episode>();
                logger.Info(
                    $"已完结剧集：{FormatItemLabel(refreshedSeries.Name, refreshedSeries.ProductionYear)}，提交刷新分集 {episodes.Count} 个");
                totalWork += episodes.Count;
                ReportProgress(totalWork, progress, submitted);
                foreach (var episode in episodes) {
                    if (cancellationToken.IsCancellationRequested) {
                        logger.Info($"计划任务已取消完结剧集分集刷新 series={target.Name}");
                        break;
                    }

                    var episodeOptions = BuildRefreshOptions(replaceMetadata, replaceImages, replaceThumbnails);
                    episodeOptions.Recursive = false;
                    _ = MetaDataRunner.RefreshMetaDataAsync(
                        episode.InternalId,
                        episodeOptions,
                        CancellationToken.None,
                        RefreshPriority.High,
                        allowFfProcess: allowFfProcess);
                    ReportProgress(totalWork, progress, ++submitted);
                }
            }

            if (cancellationToken.IsCancellationRequested) {
                logger.Info("最近条目刷新元数据计划任务已取消");
                return;
            }

            progress.Report(100.0);
            logger.Info("最近条目刷新元数据计划任务已提交后台执行");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            yield return new TaskTriggerInfo {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            };

            yield return new TaskTriggerInfo {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(16).Ticks
            };
        }

        private List<BaseItem> FetchRecentItems() {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata;
            var taskScope = taskOptions.RefreshRecentMetadataLibraries;
            var days = taskOptions.RefreshRecentMetadataDays;
            var cutoff = days > 0
                ? ConfiguredDateTime.Now.AddDays(-days)
                : (DateTime?)null;

            var items = Plugin.LibraryService.FetchRecentScheduledTaskLibraryItems(
                cutoff,
                taskScope,
                includeAudio: true);

            if (!taskOptions.EnablePremiereDateFilter || cutoff == null) return items;

            var itemsWithoutPremiereDate = items.Count(i => !i.PremiereDate.HasValue);
            var filteredItems = items
                .Where(i => !i.PremiereDate.HasValue || i.PremiereDate.Value.LocalDateTime >= cutoff.Value)
                .ToList();
            var skippedByPremiereDate = items.Count - filteredItems.Count;

            logger.Info(
                $"刷新元数据计划任务按首播日期过滤：入库时间命中 {items.Count} 个，放行 {filteredItems.Count} 个，其中缺少首播日期 {itemsWithoutPremiereDate} 个，跳过首播日期过旧 {skippedByPremiereDate} 个");
            return filteredItems;
        }

        private MetadataRefreshOptions BuildRefreshOptions(bool replaceMetadata, bool replaceImages,
            bool replaceThumbnails) {
            return new MetadataRefreshOptions(new DirectoryService(logger, Plugin.FileSystem)) {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = replaceMetadata,
                ReplaceAllImages = replaceImages,
                ReplaceThumbnailImages = replaceThumbnails,
                EnableThumbnailImageExtraction = Plugin.Instance.Options.MainPage.PlugginEnabled,
                IsAutomated = true
            };
        }

        private List<SeriesRefreshTarget> CollectSeriesRefreshTargets(IEnumerable<BaseItem> items) {
            var result = new List<SeriesRefreshTarget>();
            var seen = new HashSet<long>();

            foreach (var item in items) {
                var series = item switch {
                    Series currentSeries => currentSeries,
                    Season season => season.Series,
                    Episode episode => episode.Series,
                    _ => null
                };

                if (series == null || series.InternalId <= 0) continue;

                if (seen.Add(series.InternalId))
                    result.Add(new SeriesRefreshTarget {
                        ItemId = series.InternalId,
                        Name = series.Name,
                        ProductionYear = series.ProductionYear
                    });
            }

            return result;
        }

        private static string FormatItemLabel(string name, int? productionYear) {
            return productionYear.HasValue
                ? $"{name} ({productionYear.Value})"
                : name ?? string.Empty;
        }

        private static void ReportProgress(int totalWork, IProgress<double> progress, int completed) {
            progress?.Report(completed / (double)totalWork * 100);
        }

        private bool ShouldReplaceMetadata() {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.RefreshMetadataMode ==
                   RefreshModeOption.Replace;
        }

        private bool ShouldReplaceImages() {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingImages;
        }

        private bool ShouldReplaceThumbnails() {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata
                .ReplaceExistingVideoPreviewThumbnails;
        }

        private bool ShouldAllowFfProcess() {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.AllowFfProcess;
        }

        private sealed class SeriesRefreshTarget {
            public long ItemId { get; set; }

            public string Name { get; set; }

            public int? ProductionYear { get; set; }
        }
    }
}
