using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Services
{
    public static class MediaInfoTaskRunner
    {
        public static async Task ProcessItemsAsync(
            IReadOnlyList<BaseItem> items,
            Func<BaseItem, Task> processor,
            ILogger logger,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            if (items == null || items.Count == 0)
            {
                progress?.Report(100.0);
                return;
            }

            var maxConcurrent = Math.Max(1, Plugin.Instance.Options.General.MaxConcurrentCount);

            using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            var tasks = new List<Task>(items.Count);
            var total = items.Count;
            var completed = 0;

            foreach (var item in items)
            {
                try
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    semaphore.Release();
                    break;
                }

                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        await processor(taskItem).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"任务执行失败: {taskItem.Path ?? taskItem.Name}");
                        logger?.Error(ex.Message);
                        logger?.Debug(ex.StackTrace);
                    }
                    finally
                    {
                        semaphore.Release();
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(done / (double)total * 100);
                    }
                }, cancellationToken);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
