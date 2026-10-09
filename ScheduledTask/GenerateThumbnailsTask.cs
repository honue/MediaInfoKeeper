using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.ScheduledTask {
    public class GenerateThumbnailsTask : IScheduledTask {
        private readonly ILogger logger;

        public GenerateThumbnailsTask(ILogManager logManager) {
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperGenerateThumbnailsTask";

        public string Name => "10.补齐缺失的视频预览缩略图";

        public string Description =>
            "按本任务选择的媒体库，逐条为缺少时间轴预览缩略图的视频强制生成；已有内容不会被替换，也不会刷新影片元数据或普通图片。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            var scope = Plugin.Instance.Options.MainPage
                .ScheduledTasksEditor.GenerateThumbnails.Libraries;
            var directoryService = new DirectoryService(logger, Plugin.FileSystem);
            var candidates = Plugin.LibraryService
                .FetchScheduledTaskLibraryItems(scope)
                .OfType<Video>()
                .Where(video => video.SupportsThumbnails)
                .ToList();
            var videos = candidates
                .Where(video => !HasValidThumbnailSet(video, directoryService))
                .ToList();

            var skipped = candidates.Count - videos.Count;
            var completed = 0;
            var succeeded = 0;
            var failed = 0;

            logger.Info($"补齐视频预览缩略图：候选 {candidates.Count}，缺失 {videos.Count}，跳过 {skipped}");
            if (videos.Count == 0) {
                progress.Report(100.0);
                return;
            }

            var thumbnailGenerator = ResolveThumbnailGenerator();
            foreach (var video in videos) {
                cancellationToken.ThrowIfCancellationRequested();
                await ProcessVideoAsync(video).ConfigureAwait(false);
            }
            progress.Report(100.0);
            logger.Info($"补齐视频预览缩略图完成：成功 {succeeded}，跳过 {skipped}，失败 {failed}");

            async Task ProcessVideoAsync(Video video) {
                try {
                    var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(video);
                    var mediaSource = video.GetMediaSources(false, false, libraryOptions)
                        .FirstOrDefault();
                    if (mediaSource == null)
                        throw new InvalidOperationException("未找到媒体源，请先运行 08.补齐缺失的媒体信息");

                    var videoStream = mediaSource.MediaStreams?
                        .FirstOrDefault(stream => stream?.Type == MediaStreamType.Video)
                        ?? video.GetMediaStreams()
                            .FirstOrDefault(stream => stream?.Type == MediaStreamType.Video);
                    if (videoStream == null)
                        throw new InvalidOperationException("未找到视频流，请先运行 08.补齐缺失的媒体信息");

                    using (FfProcessGuard.Allow())
                        await thumbnailGenerator.CreateAsync(
                                video,
                                mediaSource,
                                videoStream,
                                libraryOptions,
                                cancellationToken)
                            .ConfigureAwait(false);

                    if (HasValidThumbnailSet(video, directoryService))
                        Interlocked.Increment(ref succeeded);
                    else {
                        Interlocked.Increment(ref failed);
                        logger.Error($"视频预览缩略图生成后仍未找到文件 item={video.FileName ?? video.Path}");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                    throw;
                }
                catch (Exception ex) {
                    Interlocked.Increment(ref failed);
                    logger.Error($"视频预览缩略图生成失败 item={video.FileName ?? video.Path}");
                    logger.Error(ex.Message);
                    logger.Debug(ex.StackTrace);
                }
                finally {
                    var done = Interlocked.Increment(ref completed);
                    progress.Report(done * 100.0 / videos.Count);
                }
            }
        }

        private ThumbnailGeneratorRuntime ResolveThumbnailGenerator() {
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

        private static bool HasValidThumbnailSet(Video video, IDirectoryService directoryService) {
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
