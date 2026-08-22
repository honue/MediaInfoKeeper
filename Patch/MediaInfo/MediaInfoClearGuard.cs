using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     防止 STRM 探测失败时以空列表覆盖数据库中的主视频、音频流。
    ///     对外挂字幕和音轨仍按物理文件状态正常执行新增、更新和删除。
    /// </summary>
    internal static class MediaInfoClearGuard {
        private static readonly AsyncLocal<int> AllowScopeCount = new();
        private static Harmony harmony;
        private static MethodInfo saveMediaStreamsMethod;
        private static ILogger logger;
        private static bool isEnabled;
        private static bool isPatched;

        public static bool IsReady => harmony != null && saveMediaStreamsMethod != null && (!isEnabled || isPatched);

        public static void Initialize(ILogger pluginLogger, bool enabled) {
            if (harmony != null) {
                Configure(enabled);
                return;
            }

            logger = pluginLogger;
            isEnabled = enabled;
            if (!enabled) return;

            try {
                var embyServerImplementations = Assembly.Load("Emby.Server.Implementations");
                var sqliteItemRepositoryType =
                    embyServerImplementations?.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
                if (sqliteItemRepositoryType == null) {
                    PatchLog.InitFailed(logger, nameof(MediaInfoClearGuard), "未找到 SqliteItemRepository 类型");
                    return;
                }

                var assemblyVersion = embyServerImplementations.GetName().Version;
                saveMediaStreamsMethod = PatchMethodResolver.Resolve(
                    sqliteItemRepositoryType,
                    assemblyVersion,
                    new MethodSignatureProfile {
                        Name = "sqliteitemrepository-savemediastreams-exact",
                        MethodName = "SaveMediaStreams",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] {
                            typeof(long),
                            typeof(List<MediaStream>),
                            typeof(CancellationToken)
                        }
                    },
                    logger,
                    "MediaInfoClearGuard.SaveMediaStreams");

                if (saveMediaStreamsMethod == null) {
                    PatchLog.InitFailed(logger, nameof(MediaInfoClearGuard), "未命中 SaveMediaStreams");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.ffprobe-strm-empty-media-guard");
                if (isEnabled) Patch();
            }
            catch (Exception ex) {
                PatchLog.InitFailed(logger, nameof(MediaInfoClearGuard), ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
                saveMediaStreamsMethod = null;
                isPatched = false;
            }
        }

        public static void Configure(bool enabled) {
            isEnabled = enabled;
            if (harmony == null) return;

            if (isEnabled)
                Patch();
            else
                Unpatch();
        }

        public static IDisposable Allow() {
            var previousScopeCount = AllowScopeCount.Value;
            AllowScopeCount.Value = previousScopeCount + 1;
            return new AllowSaveScope(previousScopeCount);
        }

        private static void Patch() {
            if (isPatched || harmony == null || saveMediaStreamsMethod == null) return;

            PatchLog.Patched(logger, nameof(MediaInfoClearGuard), saveMediaStreamsMethod);
            harmony.Patch(
                saveMediaStreamsMethod,
                new HarmonyMethod(typeof(MediaInfoClearGuard), nameof(SaveMediaStreamsPrefix)));
            isPatched = true;
        }

        private static void Unpatch() {
            if (!isPatched || harmony == null || saveMediaStreamsMethod == null) return;

            harmony.Unpatch(saveMediaStreamsMethod, HarmonyPatchType.Prefix, harmony.Id);
            isPatched = false;
        }

        [HarmonyPrefix]
        private static bool SaveMediaStreamsPrefix(long itemId, ref List<MediaStream> streams, CancellationToken cancellationToken) {
            if (!isEnabled || AllowScopeCount.Value > 0) return true;

            var item = Plugin.LibraryManager?.GetItemById(itemId);
            var itemPath = item?.Path ?? item?.FileName;
            // 普通媒体文件交给 Emby 原生流程；STRM 本次已包含主流时也无需保护。
            if (item == null || !LibraryService.IsFileShortcut(itemPath) || !WillClearPrimaryMediaInfo(streams)) return true;

            var result = PrepareStreamsForSave(itemId, streams, cancellationToken, out var preparedStreams);
            switch (result) {
                case StreamSavePreparation.MergedWithExistingPrimary:
                    streams = preparedStreams;
                    logger?.Debug($"已保留媒体信息并合并有效外挂流: {item.FileName ?? item.Path}");
                    return true;
                case StreamSavePreparation.NoPrimaryToPreserve:
                    streams = preparedStreams;
                    logger?.Debug($"数据库中没有主媒体信息，已放行媒体流保存: {item.FileName ?? item.Path}");
                    return true;
                default:
                    logger?.Debug($"已阻止媒体信息丢失: {item.FileName ?? item.Path}");
                    return false;
            }
        }

        private static bool WillClearPrimaryMediaInfo(List<MediaStream> streams) {
            return streams == null ||
                   !streams.Any(IsPrimaryMediaStream);
        }

        /// <summary>
        ///     为本次保存生成安全的完整媒体流列表。
        ///     已有主流时进行保护性合并；确认数据库本来就没有主流时直接放行有效输入；读取失败时阻止保存。
        /// </summary>
        private static StreamSavePreparation PrepareStreamsForSave(
            long itemId,
            List<MediaStream> incomingStreams,
            CancellationToken cancellationToken,
            out List<MediaStream> preparedStreams) {
            preparedStreams = null;
            if (incomingStreams == null) return StreamSavePreparation.Failed;

            try {
                var existingStreams = Plugin.Instance?.ItemRepository
                    ?.GetMediaStreams(new MediaStreamQuery { ItemId = itemId }, cancellationToken);
                if (existingStreams == null) return StreamSavePreparation.Failed;

                if (!existingStreams.Any(IsPrimaryMediaStream)) {
                    // Emby 刷新和本插件的外挂扫描都会传入当前完整外挂流列表；字幕提供插件只返回
                    // 字幕内容，不会增量调用 SaveMediaStreams。因此没有主流可保护时应按本次列表全量覆盖。
                    preparedStreams = incomingStreams
                        .Where(stream => !IsExternalFileStream(stream) || ExternalFileExists(stream))
                        .ToList();
                    return StreamSavePreparation.NoPrimaryToPreserve;
                }

                preparedStreams = MergeWithExistingPrimaryStreams(existingStreams, incomingStreams);
                return StreamSavePreparation.MergedWithExistingPrimary;
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                logger?.Warn($"合并外挂字幕保存失败，继续阻止媒体信息丢失: {ex.Message}");
                logger?.Debug(ex.StackTrace);
                return StreamSavePreparation.Failed;
            }
        }

        private static List<MediaStream> MergeWithExistingPrimaryStreams(
            List<MediaStream> existingStreams,
            List<MediaStream> incomingStreams) {
            // SaveMediaStreams 是全量覆盖操作：保留旧主流，并用本次扫描结果替换同路径外挂流。
            var validIncomingExternalStreams = incomingStreams
                .Where(stream => IsExternalFileStream(stream) && ExternalFileExists(stream))
                .ToList();
            var incomingExternalPaths = new HashSet<string>(
                validIncomingExternalStreams.Select(stream => stream.Path.Trim()),
                StringComparer.OrdinalIgnoreCase);
            // 删除物理文件后不再恢复旧记录；同路径的新扫描结果将在下面重新追加。
            var mergedStreams = existingStreams
                .Where(stream =>
                    stream != null &&
                    (!IsExternalFileStream(stream) ||
                     ExternalFileExists(stream) &&
                     !incomingExternalPaths.Contains(stream.Path.Trim())))
                .ToList();
            var nextIndex = mergedStreams.Count == 0 ? 0 : mergedStreams.Max(stream => stream.Index) + 1;

            foreach (var externalStream in validIncomingExternalStreams) {
                externalStream.Index = nextIndex++;
                mergedStreams.Add(externalStream);
            }

            return mergedStreams;
        }

        private static bool IsPrimaryMediaStream(MediaStream stream) {
            return stream != null &&
                   !stream.IsExternal &&
                   (stream.Type == MediaStreamType.Video || stream.Type == MediaStreamType.Audio);
        }

        private static bool IsExternalFileStream(MediaStream stream) {
            return stream != null &&
                   stream.IsExternal &&
                   (stream.Type == MediaStreamType.Subtitle || stream.Type == MediaStreamType.Audio) &&
                   stream.Protocol == MediaProtocol.File &&
                   !string.IsNullOrWhiteSpace(stream.Path);
        }

        private static bool ExternalFileExists(MediaStream stream) {
            try {
                return Plugin.FileSystem?.FileExists(stream.Path) ?? true;
            }
            catch (Exception ex) {
                logger?.Debug($"检查外挂文件是否存在失败，暂时保留媒体流: {stream.Path}; {ex.Message}");
                return true;
            }
        }

        private enum StreamSavePreparation {
            Failed,
            MergedWithExistingPrimary,
            NoPrimaryToPreserve
        }

        private sealed class AllowSaveScope : IDisposable {
            private readonly int previousScopeCount;

            public AllowSaveScope(int previousScopeCount) {
                this.previousScopeCount = previousScopeCount;
            }

            public void Dispose() {
                AllowScopeCount.Value = previousScopeCount;
            }
        }
    }
}
