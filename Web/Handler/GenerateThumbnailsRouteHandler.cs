using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.Web.Handler {
    internal sealed class GenerateThumbnailsRouteHandler {
        // 服务按请求创建，去重集合、队列和生成器必须是静态的，才能跨请求生效。
        private static readonly ConcurrentDictionary<long, byte> Pending = new();
        private static readonly ConcurrentQueue<QueueEntry> Queue = new();
        private static readonly object WorkerLock = new();
        private static CancellationTokenSource shutdown = new();
        private static bool workerRunning;
        private static ThumbnailGeneratorRuntime generator;

        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public GenerateThumbnailsRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems) {
            _expandToTargetItems = expandToTargetItems;
        }

        // 插件卸载时取消正在排队和生成的任务
        public static void Shutdown() {
            var previous = Interlocked.Exchange(ref shutdown, new CancellationTokenSource());
            previous.Cancel();
            previous.Dispose();
        }

        public MediaInfoMenuResponse Handle(GenerateThumbnailsRequest request) {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0) {
                response.Message = "no items";
                return response;
            }

            if (Plugin.Instance.Options.MainPage?.PlugginEnabled != true) {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "plugin disabled";
                return response;
            }

            var candidates = _expandToTargetItems(request.Ids)
                .OfType<Video>()
                .Where(video => video.SupportsThumbnails)
                .ToList();
            response.Total = candidates.Count;

            if (candidates.Count == 0) {
                response.Message = "no supported items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu GenerateThumbnails result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var videos = candidates.Where(video => !HasValidThumbnailSet(video)).ToList();
            response.Skipped = candidates.Count - videos.Count;

            if (videos.Count == 0) {
                response.Message = "thumbnail sets already exist";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu GenerateThumbnails result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            ThumbnailGeneratorRuntime runtime;
            try {
                runtime = GetGenerator();
            }
            catch (Exception ex) {
                response.Failed = videos.Count;
                response.Message = "thumbnail generator unavailable";
                Plugin.Instance.Logger.Error("快捷菜单生成视频预览缩略图失败：未找到兼容的缩略图生成器");
                Plugin.Instance.Logger.Error(ex.Message);
                Plugin.Instance.Logger.Debug(ex.StackTrace);
                return response;
            }

            foreach (var video in videos) {
                // 已在生成中的视频不重复提交
                if (!Pending.TryAdd(video.InternalId, 0)) {
                    response.Skipped++;
                    continue;
                }

                Queue.Enqueue(new QueueEntry(video, runtime));
                response.Processed++;
                response.Succeeded++;
            }

            EnsureWorker();
            response.Message = "submitted";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu GenerateThumbnails result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }

        // 只保留一个后台工作线程，按顺序逐个生成，避免整库提交时同时开出大量任务
        private static void EnsureWorker() {
            lock (WorkerLock) {
                if (workerRunning)
                    return;
                workerRunning = true;
            }

            var token = shutdown.Token;
            _ = Task.Run(() => RunWorkerAsync(token));
        }

        private static async Task RunWorkerAsync(CancellationToken cancellationToken) {
            try {
                while (!cancellationToken.IsCancellationRequested) {
                    if (!Queue.TryDequeue(out var entry)) {
                        lock (WorkerLock) {
                            if (Queue.IsEmpty) {
                                workerRunning = false;
                                return;
                            }
                        }

                        continue;
                    }

                    await GenerateAsync(entry, cancellationToken).ConfigureAwait(false);
                }
            }
            finally {
                if (cancellationToken.IsCancellationRequested) {
                    // 被取消时队列里剩下的条目不会再执行，这里一并清理去重标记
                    while (Queue.TryDequeue(out var dropped))
                        Pending.TryRemove(dropped.Video.InternalId, out _);

                    lock (WorkerLock)
                        workerRunning = false;
                }
            }
        }

        private static async Task GenerateAsync(QueueEntry entry, CancellationToken cancellationToken) {
            var video = entry.Video;
            try {
                // 排队期间可能已被其他途径生成，执行前再检查一次
                if (HasValidThumbnailSet(video))
                    return;

                var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(video);
                var mediaSource = video.GetMediaSources(false, false, libraryOptions).FirstOrDefault();
                if (mediaSource == null)
                    throw new InvalidOperationException("未找到媒体源，请先提取媒体信息");

                var videoStream = mediaSource.MediaStreams?
                                      .FirstOrDefault(stream => stream?.Type == MediaStreamType.Video)
                                  ?? video.GetMediaStreams()
                                      .FirstOrDefault(stream => stream?.Type == MediaStreamType.Video);
                if (videoStream == null)
                    throw new InvalidOperationException("未找到视频流，请先提取媒体信息");

                using (FfProcessGuard.Allow())
                    await entry.Generator.CreateAsync(
                            video,
                            mediaSource,
                            videoStream,
                            libraryOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                Plugin.Instance.Logger.Info($"快捷菜单生成视频预览缩略图已取消: {video.Path ?? video.Name}");
            }
            catch (Exception ex) {
                Plugin.Instance.Logger.Error($"快捷菜单生成视频预览缩略图失败: {video.Path ?? video.Name}");
                Plugin.Instance.Logger.Error(ex.Message);
                Plugin.Instance.Logger.Debug(ex.StackTrace);
            }
            finally {
                Pending.TryRemove(video.InternalId, out _);
            }
        }

        private static bool HasValidThumbnailSet(Video video) {
            var directoryService = new DirectoryService(Plugin.SharedLogger, Plugin.FileSystem);
            var thumbnailSets = Video.GetThumbnailSetInfos(
                                    video.Path,
                                    video.Id,
                                    directoryService,
                                    0,
                                    false)
                                ?? Array.Empty<ThumbnailSetInfo>();

            return thumbnailSets.Any(set =>
                !string.IsNullOrWhiteSpace(set.Path) &&
                (File.Exists(set.Path) || Directory.Exists(set.Path)));
        }

        private static ThumbnailGeneratorRuntime GetGenerator() {
            return generator ?? (generator = ResolveThumbnailGenerator());
        }

        private sealed class QueueEntry {
            public QueueEntry(Video video, ThumbnailGeneratorRuntime generator) {
                Video = video;
                Generator = generator;
            }

            public Video Video { get; }
            public ThumbnailGeneratorRuntime Generator { get; }
        }

        private static ThumbnailGeneratorRuntime ResolveThumbnailGenerator() {
            var logger = Plugin.Instance.Logger;
            var assembly = Assembly.Load("Emby.Providers");
            var type = assembly.GetType("Emby.Providers.MediaInfo.ThumbnailGenerator", true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic;
            var methods = type.GetMethods(flags)
                .Where(candidate => candidate.Name == "CreateThumbnailSets")
                .ToArray();
            var method = methods.FirstOrDefault(IsSupportedGeneratorMethod);
            if (method == null) {
                var signatures = methods.Length == 0
                    ? string.Join("；", type.GetMethods(flags).Select(FormatMethod))
                    : string.Join("；", methods.Select(FormatMethod));
                logger.Error($"未找到兼容的视频预览缩略图生成方法。运行时方法：{signatures}");
                throw new MissingMethodException(type.FullName, "CreateThumbnailSets");
            }

            var instance = method.IsStatic ? null : Plugin.Instance.AppHost.CreateInstance(type);
            logger.Info(
                $"视频预览缩略图生成器：{type.FullName}.{FormatMethod(method)}，程序集 {assembly.GetName().Version}");
            return new ThumbnailGeneratorRuntime(instance, method);
        }

        private static bool IsSupportedGeneratorMethod(MethodInfo method) {
            if (!typeof(Task).IsAssignableFrom(method.ReturnType))
                return false;

            var parameters = method.GetParameters();
            return parameters.Length == 8 &&
                   parameters[0].ParameterType.IsAssignableFrom(typeof(Video)) &&
                   parameters[1].ParameterType == typeof(MediaSourceInfo) &&
                   parameters[2].ParameterType == typeof(string) &&
                   parameters[3].ParameterType == typeof(MediaProtocol) &&
                   parameters[4].ParameterType == typeof(MediaContainers?) &&
                   parameters[5].ParameterType == typeof(MediaStream) &&
                   parameters[6].ParameterType == typeof(LibraryOptions) &&
                   parameters[7].ParameterType == typeof(CancellationToken);
        }

        private static string FormatMethod(MethodInfo method) {
            var parameters = string.Join(",", method.GetParameters()
                .Select(parameter => parameter.ParameterType.FullName));
            return $"{method.Name}({parameters}) -> {method.ReturnType.FullName}";
        }

        private sealed class ThumbnailGeneratorRuntime {
            private readonly object instance;
            private readonly MethodInfo method;

            public ThumbnailGeneratorRuntime(object instance, MethodInfo method) {
                this.instance = instance;
                this.method = method;
            }

            public async Task CreateAsync(
                Video video,
                MediaSourceInfo mediaSource,
                MediaStream videoStream,
                LibraryOptions libraryOptions,
                CancellationToken cancellationToken) {
                var path = mediaSource.Path ?? video.Path;
                MediaContainers? container = null;
                if (!string.IsNullOrWhiteSpace(mediaSource.Container) &&
                    Enum.TryParse(mediaSource.Container, true, out MediaContainers parsedContainer))
                    container = parsedContainer;

                var result = method.Invoke(instance, new object[] {
                    video,
                    mediaSource,
                    path,
                    mediaSource.Protocol,
                    container,
                    videoStream,
                    libraryOptions,
                    cancellationToken
                });
                if (result is not Task task)
                    throw new InvalidOperationException("Emby 缩略图生成器未返回异步任务");

                await task.ConfigureAwait(false);
            }
        }
    }
}
