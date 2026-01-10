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

            var replace = ShouldReplace();
            this.logger.Info($"计划任务条目数 {total}，覆盖模式={replace}");

            var current = 0;
            foreach (var item in items)
            {
                var created = item.DateCreated == default ? "unknown" : item.DateCreated.ToString("u");
                this.logger.Info($"[{current + 1}/{total}] 刷新 {item.Path ?? item.Name} Created={created}");

                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("计划任务已取消");
                    return;
                }

                try
                {
                    var options = BuildRefreshOptions(replace);
                    var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(item);
                    var libraryOptions = this.libraryManager.GetLibraryOptions(item);

                    using (MetadataProvidersGuard.Allow())
                    {
                        await Plugin.ProviderManager
                            .RefreshSingleItem(item, options, collectionFolders, libraryOptions, cancellationToken)
                            .ConfigureAwait(false);
                    }
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

        public string Key => "MediaInfoKeeperRefreshRecentMetadataTask";

        public string Name => "刷新最近入库的元数据";

        public string Description => "按配置范围刷新最近入库条目的元数据，可选覆盖或补全。";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        private List<BaseItem> FetchRecentItems()
        {
            var limit = Math.Max(1, Plugin.Instance.Options.RecentItemsLimit);

            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var cutoff = Plugin.Instance.Options.RecentItemsDays > 0
                ? DateTime.UtcNow.AddDays(-Plugin.Instance.Options.RecentItemsDays)
                : (DateTime?)null;

            var items = this.libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null)
                .Where(i => cutoff == null || i.DateCreated >= cutoff)
                .OrderByDescending(i => i.DateCreated)
                .Take(limit)
                .ToList();

            return items;
        }

        private MetadataRefreshOptions BuildRefreshOptions(bool replace)
        {
            var directoryService = new DirectoryService(this.logger, Plugin.FileSystem);
            return new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = replace ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
                ImageRefreshMode = replace ? MetadataRefreshMode.FullRefresh : MetadataRefreshMode.Default,
                ReplaceAllMetadata = replace,
                ReplaceAllImages = replace
            };
        }

        private bool ShouldReplace()
        {
            var mode = Plugin.Instance.Options.RefreshMetadataMode ?? string.Empty;
            var tokens = mode.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Any(v => v.Equals("Replace", StringComparison.OrdinalIgnoreCase));
        }
    }
}
