using System;
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
        private readonly IDirectoryService _directoryService;
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;
        private ThumbnailGeneratorRuntime _generator;

        public GenerateThumbnailsRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems) {
            _expandToTargetItems = expandToTargetItems;
            _directoryService = new DirectoryService(Plugin.SharedLogger, Plugin.FileSystem);
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

            ThumbnailGeneratorRuntime generator;
            try {
                generator = GetGenerator();
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
                response.Processed++;
                try {
                    QueueThumbnailSetGeneration(video, generator);
                    response.Succeeded++;
                }
                catch (Exception ex) {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单生成视频预览缩略图失败: {video.Path ?? video.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = "submitted";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu GenerateThumbnails result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }

        private static void QueueThumbnailSetGeneration(Video video, ThumbnailGeneratorRuntime generator) {
            _ = Task.Run(async () => {
                try {
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
                        await generator.CreateAsync(
                                video,
                                mediaSource,
                                videoStream,
                                libraryOptions,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                }
                catch (Exception ex) {
                    Plugin.Instance.Logger.Error($"快捷菜单生成视频预览缩略图失败: {video.Path ?? video.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            });
        }

        private bool HasValidThumbnailSet(Video video) {
            var thumbnailSets = Video.GetThumbnailSetInfos(
                                    video.Path,
                                    video.Id,
                                    _directoryService,
                                    0,
                                    false)
                                ?? Array.Empty<ThumbnailSetInfo>();

            return thumbnailSets.Any(set =>
                !string.IsNullOrWhiteSpace(set.Path) &&
                (File.Exists(set.Path) || Directory.Exists(set.Path)));
        }

        private ThumbnailGeneratorRuntime GetGenerator() {
            return _generator ?? (_generator = ResolveThumbnailGenerator());
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
