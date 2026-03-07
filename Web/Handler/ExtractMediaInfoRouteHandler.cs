using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class ExtractMediaInfoRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public ExtractMediaInfoRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems)
        {
            _expandToTargetItems = expandToTargetItems;
        }

        public async Task<MediaInfoMenuResponse> HandleAsync(ExtractMediaInfoRequest request)
        {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "no items";
                return response;
            }

            if (Plugin.Instance.Options.MainPage?.PlugginEnabled != true)
            {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "plugin disabled";
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids);
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "no supported items";
                return response;
            }

            foreach (var item in targetItems)
            {
                response.Processed++;
                try
                {
                    var result = await ExtractSingleItemAsync(item).ConfigureAwait(false);
                    if (result)
                    {
                        response.Succeeded++;
                    }
                    else
                    {
                        response.Skipped++;
                    }
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单提取媒体信息失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = "ok";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu ExtractMediaInfo result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }

        private static async Task<bool> ExtractSingleItemAsync(BaseItem item)
        {
            if (!(item is Video))
            {
                return false;
            }

            var displayName = item.FileName ?? item.Path ?? item.Name;
            using (FfprobeGuard.Allow())
            {
                var filePath = item.Path;
                if (string.IsNullOrEmpty(filePath))
                {
                    Plugin.Instance.Logger.Info($"快捷菜单提取媒体信息跳过 无路径: {displayName}");
                    return false;
                }

                var refreshOptions = Plugin.MediaInfoService.GetMediaInfoRefreshOptions();
                var directoryService = refreshOptions.DirectoryService;

                if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                    uri.Scheme == Uri.UriSchemeFile)
                {
                    var file = directoryService.GetFile(filePath);
                    if (file?.Exists != true)
                    {
                        Plugin.Instance.Logger.Info($"快捷菜单提取媒体信息跳过 文件不存在: {displayName}");
                        return false;
                    }
                }

                if (Plugin.LibraryService.HasMediaInfo(item))
                {
                    Plugin.Instance.Logger.Info($"快捷菜单提取媒体信息跳过 已存在MediaInfo: {displayName}");
                    return false;
                }

                var deserializeResult = await Plugin.MediaInfoService
                    .DeserializeMediaInfo(item, directoryService, "ShortcutMenu Restore")
                    .ConfigureAwait(false);

                if (deserializeResult == MediaInfoService.MediaInfoRestoreResult.Restored ||
                    deserializeResult == MediaInfoService.MediaInfoRestoreResult.AlreadyExists)
                {
                    await Plugin.MediaInfoService
                        .SerializeMediaInfo(item.InternalId, directoryService, true, "ShortcutMenu Restored")
                        .ConfigureAwait(false);
                    return true;
                }

                var collectionFolders = Plugin.LibraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
                var libraryOptions = Plugin.LibraryManager.GetLibraryOptions(item);
                var copiedOptions = LibraryService.CopyLibraryOptions(libraryOptions);

                item.DateLastRefreshed = new DateTimeOffset();
                await Plugin.ProviderManager
                    .RefreshSingleItem(item, refreshOptions, collectionFolders, copiedOptions, CancellationToken.None)
                    .ConfigureAwait(false);

                if (!Plugin.LibraryService.HasMediaInfo(item))
                {
                    Plugin.Instance.Logger.Info($"快捷菜单提取媒体信息失败 无媒体流: {displayName}");
                    return false;
                }

                await Plugin.MediaInfoService
                    .SerializeMediaInfo(item.InternalId, directoryService, true, "ShortcutMenu Extract")
                    .ConfigureAwait(false);
                return true;
            }
        }
    }
}
