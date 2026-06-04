using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class ScanExternalFilesTask : IScheduledTask
    {
        private readonly ILogger logger;

        public ScanExternalFilesTask(ILogManager logManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperScanExternalFilesTask";

        public string Name => "08.扫描外挂文件";

        public string Description => "按本任务配置的媒体库范围独立扫描外挂文件，发现字幕或音轨变更时更新媒体流。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("外挂文件扫描计划任务开始");

            if (Plugin.ExternalFiles == null || !Plugin.ExternalFiles.IsAvailable)
            {
                this.logger.Warn("外挂文件扫描不可用，缺少 Emby 所需依赖");
                progress?.Report(100.0);
                return;
            }

            var items = FetchScopedItems();
            var total = items.Count;
            if (total == 0)
            {
                progress?.Report(100.0);
                this.logger.Info("外挂文件扫描计划任务完成，条目数 0");
                return;
            }

            var refreshOptions = Plugin.ExternalFiles.GetRefreshOptions();
            var completed = 0;
            var tasks = items
                .Select(async item =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ProcessItemAsync(item, refreshOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        this.logger.Info($"外挂文件扫描已取消: {item.Path ?? item.Name}");
                    }
                    catch (Exception ex)
                    {
                        this.logger.Error($"外挂文件扫描失败: {item.Path ?? item.Name}");
                        this.logger.Error(ex.Message);
                        this.logger.Debug(ex.StackTrace);
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(done / (double)total * 100);
                    }
                })
                .ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            this.logger.Info("外挂文件扫描计划任务完成");
        }

        private List<BaseItem> FetchScopedItems()
        {
            var items = Plugin.LibraryService.FetchScheduledTaskLibraryItems(
                Plugin.Instance.Options.MainPage.ScheduledTasksEditor.ScanExternalFiles.ScanExternalFilesLibraries);
            this.logger.Info($"外挂文件扫描条目数 {items.Count}");
            return items;
        }

        private async Task ProcessItemAsync(
            BaseItem item,
            MediaBrowser.Controller.Providers.MetadataRefreshOptions refreshOptions,
            CancellationToken cancellationToken)
        {
            var displayName = item.FileName ?? item.Path ?? item.Name;

            if (!Plugin.ExternalFiles.HasExternalFilesChanged(item, refreshOptions.DirectoryService, true))
            {
                return;
            }

            await Plugin.ExternalFiles
                .UpdateExternalFiles(item, refreshOptions, false, cancellationToken)
                .ConfigureAwait(false);
            this.logger.Info($"外挂文件已更新: {displayName}");
        }
    }
}
