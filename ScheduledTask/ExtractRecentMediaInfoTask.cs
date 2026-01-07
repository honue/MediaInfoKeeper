using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ExtractRecentMediaInfoTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public ExtractRecentMediaInfoTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("计划任务执行");

            var items = FetchRecentScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成(0 个条目)");
                return;
            }

            var current = 0;
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this.logger.Info("计划任务已取消");
                    return;
                }

                try
                {
                    await Plugin.LibraryService
                        .OrchestrateMediaInfoProcessAsync(item, "Recent Scheduled Task", cancellationToken)
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

            this.logger.Info("计划任务完成");
        }

        public string Key => "MediaInfoKeeperExtractRecentMediaInfoTask";

        public string Name => "最近条目提取媒体信息";

        public string Description => "对全局最近入库条目提取媒体信息并写入 JSON。";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        private List<BaseItem> FetchRecentScopedItems()
        {
            var limit = Math.Max(1, Plugin.Instance.Options.RecentItemsLimit);
            var scopePaths = GetScopedLibraryPaths(out var hasScope);
            if (hasScope && !scopePaths.Any())
            {
                this.logger.Info("计划任务条目数 0(范围内未匹配到媒体库)");
                return new List<BaseItem>();
            }

            var query = new InternalItemsQuery
            {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            if (scopePaths.Any())
            {
                query.PathStartsWithAny = scopePaths.ToArray();
            }

            var items = this.libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null)
                .OrderByDescending(i => i.DateCreated)
                .Take(limit)
                .ToList();

            this.logger.Info($"计划任务条目数 {items.Count}");
            return items;
        }

        private List<string> GetScopedLibraryPaths(out bool hasScope)
        {
            var scoped = Plugin.Instance.Options.ScheduledTaskLibraries ?? string.Empty;
            var tokens = new HashSet<string>(
                scoped
                    .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => value.Trim())
                    .Where(value => !string.IsNullOrEmpty(value)),
                StringComparer.OrdinalIgnoreCase);

            hasScope = tokens.Count > 0;
            var libraries = this.libraryManager.GetVirtualFolders();
            if (tokens.Count > 0)
            {
                libraries = libraries
                    .Where(folder =>
                        (!string.IsNullOrWhiteSpace(folder.ItemId) && tokens.Contains(folder.ItemId)) ||
                        (!string.IsNullOrWhiteSpace(folder.Name) && tokens.Contains(folder.Name.Trim())))
                    .ToList();
            }

            var separator = Path.DirectorySeparatorChar.ToString();
            return libraries
                .SelectMany(folder => folder.Locations ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.EndsWith(separator, StringComparison.Ordinal)
                    ? path
                    : path + separator)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
