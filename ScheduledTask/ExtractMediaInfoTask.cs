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
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ExtractMediaInfoTask : IScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public ExtractMediaInfoTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }

        public string Key => "MediaInfoKeeperExtractMediaInfoTask";

        public string Name => "提取媒体信息（范围内）";

        public string Description => "对计划任务范围内的条目 恢复/提取 媒体信息并写入 JSON。（已存在则恢复）";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("计划任务执行");

            var items = FetchScopedItems();
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
                    await ProcessItemAsync(item, "Scheduled Task", cancellationToken)
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

        private List<BaseItem> FetchScopedItems()
        {
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
                .ToList();

            this.logger.Info($"计划任务条目数 {items.Count}");
            return items;
        }

        private async Task ProcessItemAsync(BaseItem item, string source, CancellationToken cancellationToken)
        {
            var displayName = item.Path ?? item.Name;

            if (!Plugin.LibraryService.IsItemInScope(item))
            {
                this.logger.Info($"跳过 不在库范围: {displayName}");
                return;
            }

            var persistMediaInfo = item is Video && Plugin.Instance.Options.General.PersistMediaInfoEnabled;
            if (!persistMediaInfo)
            {
                this.logger.Info($"跳过 未开启持久化或非视频: {displayName}");
                return;
            }

            using (FfprobeGuard.Allow())
            using (MetadataProvidersGuard.Allow())
            {
                var filePath = item.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    this.logger.Info($"跳过 无路径: {displayName}");
                    return;
                }

                var refreshOptions = Plugin.MediaInfoService.GetMediaInfoRefreshOptions();
                var directoryService = refreshOptions.DirectoryService;

                if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                    uri.Scheme == Uri.UriSchemeFile)
                {
                    var file = directoryService.GetFile(filePath);
                    if (file?.Exists != true)
                    {
                        this.logger.Info($"跳过 文件不存在: {displayName}");
                        return;
                    }
                }

                var collectionFolders = (BaseItem[])this.libraryManager.GetCollectionFolders(item);
                var libraryOptions = this.libraryManager.GetLibraryOptions(item);

                var dummyLibraryOptions = LibraryService.CopyLibraryOptions(libraryOptions);
                dummyLibraryOptions.DisabledLocalMetadataReaders = new[] { "Nfo" };
                dummyLibraryOptions.MetadataSavers = Array.Empty<string>();

                foreach (var option in dummyLibraryOptions.TypeOptions)
                {
                    option.MetadataFetchers = Array.Empty<string>();
                    option.ImageFetchers = Array.Empty<string>();
                }

                var deserializeResult = await Plugin.MediaInfoService
                    .DeserializeMediaInfo(item, directoryService, source, false)
                    .ConfigureAwait(false);

                if (deserializeResult == MediaInfoService.MediaInfoRestoreResult.Restored)
                {
                    this.logger.Info($"从JSON 恢复成功: {displayName}");
                    return;
                }

                if (deserializeResult == MediaInfoService.MediaInfoRestoreResult.AlreadyExists)
                {
                    return;
                }

                this.logger.Info($"无Json媒体信息存在，刷新开始: {displayName}");
                item.DateLastRefreshed = new DateTimeOffset();

                await Plugin.ProviderManager
                    .RefreshSingleItem(item, refreshOptions, collectionFolders, dummyLibraryOptions, cancellationToken)
                    .ConfigureAwait(false);

                this.logger.Info($"写入 JSON: {displayName}");
                await Plugin.MediaInfoService.SerializeMediaInfo(item.InternalId, directoryService, true, source)
                    .ConfigureAwait(false);

                this.logger.Info($"完成: {displayName}");
            }
        }

        private List<string> GetScopedLibraryPaths(out bool hasScope)
        {
            var scoped = Plugin.Instance.Options.LibraryScope.ScheduledTaskLibraries ?? string.Empty;
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
