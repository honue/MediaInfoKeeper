using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Configuration;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class RefreshRecentMetadataTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public RefreshRecentMetadataTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }
        public string Key => "MediaInfoKeeperRefreshRecentMetadataTask";

        public string Name => "1.刷新媒体元数据（最近入库）";

        public string Description => "全局媒体库范围内，刷新最近入库条目的元数据（可选覆盖或补全），之后会从 JSON 恢复媒体信息。";

        public string Category => Plugin.TaskCategoryName;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("最近条目刷新元数据计划任务开始");

            var items = FetchRecentItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成，条目数 0");
                return;
            }

            var replaceMetadata = ShouldReplaceMetadata();
            var replaceImages = ShouldReplaceImages();
            this.logger.Info($"计划任务条目数{total}，元数据覆盖{replaceMetadata}，图片覆盖{replaceImages}");

            var current = 0;
            foreach (var item in items)
            {
                var created = item.DateCreated == default ? "unknown" : item.DateCreated.ToString("u");
                this.logger.Info($"[{current + 1}/{total}] 刷新 {item.Path ?? item.Name} 入库日期 = {created}");

                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("计划任务已取消");
                    return;
                }

                try
                {
                    var options = BuildRefreshOptions(replaceMetadata, replaceImages);
                    var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(item);
                    var libraryOptions = this.libraryManager.GetLibraryOptions(item);

                    await Plugin.ProviderManager
                        .RefreshSingleItem(item, options, collectionFolders, libraryOptions, cancellationToken)
                        .ConfigureAwait(false);
                    // 刷新完元数据要重新从json恢复媒体信息，
                    // 非strm会重新 ffprobe，但是没有allow所以会拦截，
                    // strm会丢失信息，所以重新恢复
                    var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);
                    _ = await Plugin.MediaInfoService
                        .DeserializeMediaInfo(item, directoryService, "Recent Metadata Task Restore", true)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info($"计划任务已取消 {item.Path}");
                    return;
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

            this.logger.Info("最近条目刷新元数据计划任务完成");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        private List<BaseItem> FetchRecentItems()
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var cutoff = Plugin.Instance.Options.RecentTasks.RecentItemsDays > 0
                ? DateTime.UtcNow.AddDays(-Plugin.Instance.Options.RecentTasks.RecentItemsDays)
                : (DateTime?)null;

            var items = this.libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null)
                .Where(i => cutoff == null || i.DateCreated >= cutoff)
                .OrderByDescending(i => i.DateCreated)
                .ToList();

            return items;
        }

        private MetadataRefreshOptions BuildRefreshOptions(bool replaceMetadata, bool replaceImages)
        {
            var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);
            return new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = replaceMetadata,
                ReplaceAllImages = replaceImages
            };
        }

        private bool ShouldReplaceMetadata()
        {
            var mode = Plugin.Instance.Options.RecentTasks.RefreshMetadataMode ?? string.Empty;
            return HasReplaceFlag(mode);
        }

        private bool ShouldReplaceImages()
        {
            var mode = Plugin.Instance.Options.RecentTasks.RefreshImageMode;
            if (string.IsNullOrWhiteSpace(mode))
            {
                mode = Plugin.Instance.Options.RecentTasks.RefreshMetadataMode ?? string.Empty;
            }

            return HasReplaceFlag(mode);
        }

        private static bool HasReplaceFlag(string mode)
        {
            var tokens = mode.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Any(v => v.Equals("Replace", StringComparison.OrdinalIgnoreCase));
        }
    }
}
