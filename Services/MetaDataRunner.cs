using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace MediaInfoKeeper.Services {
    /// <summary>
    ///     元数据刷新 runner，统一去重并限制插件触发的元数据刷新并发。
    /// </summary>
    public static class MetaDataRunner {
        private static readonly object QueueSync = new();

        private static readonly Queue<RefreshRequest> HighestRefreshQueue = new();

        private static readonly Queue<RefreshRequest> HighRefreshQueue = new();

        private static readonly Queue<RefreshRequest> NormalRefreshQueue = new();

        private static readonly Queue<RefreshRequest> LowRefreshQueue = new();

        private static readonly Dictionary<long, RefreshRequest> InFlightRefreshes = new();

        private static int maxConcurrentCount = 3;
        private static int activeCount;
        private static int waitingCount;

        /// <summary>
        ///     更新元数据 runner 的运行配置，避免执行热路径反复读取插件配置。
        /// </summary>
        /// <param name="maxConcurrent">最大并发数。</param>
        public static void Configure(int maxConcurrent) {
            Volatile.Write(ref maxConcurrentCount, Math.Max(1, maxConcurrent));

            lock (QueueSync) {
                StartWorkersInsideLock();
            }
        }

        /// <summary>
        ///     获取元数据刷新 runner 的实时队列状态。
        /// </summary>
        public static QueueStats GetQueueStats() {
            return new QueueStats {
                Waiting = Math.Max(0, Volatile.Read(ref waitingCount)),
                Running = Math.Max(0, Volatile.Read(ref activeCount)),
                MaxConcurrent = GetMaxConcurrent()
            };
        }

        /// <summary>
        ///     使用指定刷新选项刷新单个条目的元数据，并受插件元数据并发设置限制。
        /// </summary>
        /// <param name="internalId">Emby 条目内部 ID。</param>
        /// <param name="options">Emby 原始刷新选项。</param>
        /// <param name="cancellationToken">取消标记。</param>
        public static async Task RefreshMetaDataAsync(
            long internalId,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken = default,
            RefreshPriority priority = RefreshPriority.Normal,
            bool replaceQueued = false,
            bool allowFfProcess = false) {
            if (internalId <= 0 || options == null) return;

            var refreshTask = QueueRefresh(internalId, options, cancellationToken, priority, replaceQueued,
                allowFfProcess);
            await refreshTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///     使用插件默认的入库元数据刷新选项刷新单个条目。
        /// </summary>
        /// <param name="internalId">Emby 条目内部 ID。</param>
        /// <param name="cancellationToken">取消标记。</param>
        public static async Task RefreshMetaDataAsync(
            long internalId,
            CancellationToken cancellationToken = default,
            RefreshPriority priority = RefreshPriority.Normal,
            bool allowFfProcess = false) {
            await RefreshMetaDataAsync(internalId, GetRefreshOptions(), cancellationToken, priority,
                    allowFfProcess: allowFfProcess)
                .ConfigureAwait(false);
        }

        private static MetadataRefreshOptions GetRefreshOptions() {
            return new MetadataRefreshOptions(new DirectoryService(Plugin.SharedLogger, Plugin.FileSystem)) {
                EnableRemoteContentProbe = false,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                EnableThumbnailImageExtraction = Plugin.Instance?.Options?.MainPage?.PlugginEnabled ?? true,
                EnableSubtitleDownloading = false
            };
        }

        private static Task QueueRefresh(
            long internalId,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken,
            RefreshPriority priority = RefreshPriority.Normal,
            bool replaceQueued = false,
            bool allowFfProcess = false) {
            lock (QueueSync) {
                if (InFlightRefreshes.TryGetValue(internalId, out var existing)) {
                    if (!existing.Started && !existing.Disabled && allowFfProcess) existing.AllowFfProcess = true;

                    if (!existing.Started &&
                        !existing.Disabled &&
                        (replaceQueued || priority < existing.Priority)) {
                        existing.Disabled = true;
                        waitingCount = Math.Max(0, waitingCount - 1);
                        Volatile.Write(ref waitingCount, waitingCount);

                        var replacement = new RefreshRequest {
                            InternalId = internalId,
                            Options = options,
                            Priority = priority,
                            CancellationToken = cancellationToken,
                            AllowFfProcess = allowFfProcess,
                            Completion = existing.Completion
                        };

                        InFlightRefreshes[internalId] = replacement;
                        GetQueue(priority).Enqueue(replacement);
                        waitingCount++;
                        Volatile.Write(ref waitingCount, waitingCount);
                        StartWorkersInsideLock();
                    }

                    return existing.Completion.Task;
                }

                var request = new RefreshRequest {
                    InternalId = internalId,
                    Options = options,
                    Priority = priority,
                    CancellationToken = cancellationToken,
                    AllowFfProcess = allowFfProcess,
                    Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                };

                InFlightRefreshes[internalId] = request;
                GetQueue(priority).Enqueue(request);
                waitingCount++;
                Volatile.Write(ref waitingCount, waitingCount);
                StartWorkersInsideLock();
                return request.Completion.Task;
            }
        }

        private static Queue<RefreshRequest> GetQueue(RefreshPriority priority) {
            switch (priority) {
                case RefreshPriority.Highest:
                    return HighestRefreshQueue;
                case RefreshPriority.High:
                    return HighRefreshQueue;
                case RefreshPriority.Low:
                    return LowRefreshQueue;
                case RefreshPriority.Normal:
                default:
                    return NormalRefreshQueue;
            }
        }

        private static void StartWorkersInsideLock() {
            var maxConcurrent = GetMaxConcurrent();
            while (activeCount < maxConcurrent && waitingCount > 0) {
                activeCount++;
                Volatile.Write(ref activeCount, activeCount);
                _ = Task.Run(ProcessQueueAsync);
            }
        }

        private static async Task ProcessQueueAsync() {
            while (true) {
                RefreshRequest request;
                lock (QueueSync) {
                    if (waitingCount == 0 || activeCount > GetMaxConcurrent()) {
                        activeCount = Math.Max(0, activeCount - 1);
                        Volatile.Write(ref activeCount, activeCount);
                        return;
                    }

                    request = TakeNextRequestInsideLock();
                }

                await ProcessRefreshAsync(request).ConfigureAwait(false);
            }
        }

        private static RefreshRequest TakeNextRequestInsideLock() {
            while (true) {
                var request = GetNextQueueInsideLock().Dequeue();
                if (request.Disabled) continue;

                request.Started = true;
                waitingCount--;
                Volatile.Write(ref waitingCount, waitingCount);
                return request;
            }
        }

        private static Queue<RefreshRequest> GetNextQueueInsideLock() {
            if (HighestRefreshQueue.Count > 0) return HighestRefreshQueue;

            if (HighRefreshQueue.Count > 0) return HighRefreshQueue;

            return NormalRefreshQueue.Count > 0
                ? NormalRefreshQueue
                : LowRefreshQueue;
        }

        private static async Task ProcessRefreshAsync(RefreshRequest request) {
            try {
                request.CancellationToken.ThrowIfCancellationRequested();

                var item = Plugin.LibraryManager?.GetItemById(request.InternalId);
                if (item != null) {
                    if (ShouldExpandRecursive(request.Options) && item is Folder folder)
                        await RefreshRecursiveFolderAsync(folder, request)
                            .ConfigureAwait(false);
                    else
                        await RefreshItemAsync(item, request.Options, request.CancellationToken, request.AllowFfProcess)
                            .ConfigureAwait(false);
                }

                request.Completion.TrySetResult(true);
            }
            catch (OperationCanceledException ex) {
                request.Completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex) {
                var displayName = Plugin.LibraryManager?.GetItemById(request.InternalId) is BaseItem item
                    ? item.FileName ?? item.Path ?? item.Name
                    : request.InternalId.ToString();
                Plugin.SharedLogger?.Error($"元数据刷新失败 item={displayName}");
                Plugin.SharedLogger?.Error(ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                request.Completion.TrySetException(ex);
            }
            finally {
                lock (QueueSync) {
                    if (InFlightRefreshes.TryGetValue(request.InternalId, out var current) &&
                        ReferenceEquals(current, request))
                        InFlightRefreshes.Remove(request.InternalId);

                    StartWorkersInsideLock();
                }
            }
        }

        private static async Task RefreshItemAsync(
            BaseItem item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken,
            bool allowFfProcess) {
            if (item is Video || item is Audio) Plugin.SharedLogger?.Info("刷新元数据 {0}", item.FileName);

            await Plugin.MetaDataService
                .RefreshMetaDataAsync(item, options, cancellationToken, allowFfProcess)
                .ConfigureAwait(false);
        }

        private static async Task RefreshRecursiveFolderAsync(Folder folder, RefreshRequest request) {
            var parentOptions = new MetadataRefreshOptions(request.Options) {
                Recursive = false
            };
            await RefreshItemAsync(folder, parentOptions, request.CancellationToken, request.AllowFfProcess)
                .ConfigureAwait(false);

            foreach (var child in folder.GetRecursiveChildren()) {
                if (child == null ||
                    child.InternalId == folder.InternalId ||
                    !request.Options.RefreshItem(child))
                    continue;

                var childOptions = new MetadataRefreshOptions(request.Options) {
                    Recursive = false
                };
                _ = QueueRefresh(
                    child.InternalId,
                    childOptions,
                    CancellationToken.None,
                    request.Priority,
                    allowFfProcess: request.AllowFfProcess);
            }
        }

        private static bool ShouldExpandRecursive(MetadataRefreshOptions options) {
            return options.Recursive && HasRecursiveChildRefreshWork(options);
        }

        private static bool HasRecursiveChildRefreshWork(MetadataRefreshOptions options) {
            return options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh ||
                   options.ImageRefreshMode == MetadataRefreshMode.FullRefresh ||
                   options.ReplaceAllMetadata ||
                   options.ReplaceAllImages ||
                   options.ReplaceThumbnailImages;
        }

        private static int GetMaxConcurrent() {
            return Math.Max(1, Volatile.Read(ref maxConcurrentCount));
        }

        /// <summary>
        ///     runner 当前队列状态，用于插件设置页展示。
        /// </summary>
        public sealed class QueueStats {
            /// <summary>已进入 runner 但尚未获取并发槽的任务数。</summary>
            public int Waiting { get; set; }

            /// <summary>已获取并发槽并正在执行的任务数。</summary>
            public int Running { get; set; }

            /// <summary>当前配置允许的最大并发数。</summary>
            public int MaxConcurrent { get; set; }

            /// <summary>尚未完成的任务总数。</summary>
            public int Total => Waiting + Running;
        }

        private sealed class RefreshRequest {
            public long InternalId { get; set; }

            public MetadataRefreshOptions Options { get; set; }

            public RefreshPriority Priority { get; set; }

            public CancellationToken CancellationToken { get; set; }

            public bool AllowFfProcess { get; set; }

            public TaskCompletionSource<bool> Completion { get; set; }

            public bool Started { get; set; }

            public bool Disabled { get; set; }
        }
    }
}
