using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class DeleteMediaInfoPersistRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public DeleteMediaInfoPersistRouteHandler(
            Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems,
            ILibraryManager libraryManager,
            IItemRepository itemRepository)
        {
            _expandToTargetItems = expandToTargetItems;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public MediaInfoMenuResponse Handle(DeleteMediaInfoPersistRequest request)
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
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu DeleteMediaInfoPersist result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var directoryService = new DirectoryService(Plugin.Instance.Logger, Plugin.FileSystem);

            foreach (var item in targetItems)
            {
                response.Processed++;
                try
                {
                    if (DeleteSingleItemMediaInfo(item, directoryService))
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
                    Plugin.Instance.Logger.Error($"快捷菜单删除媒体信息失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = "ok";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu DeleteMediaInfoPersist result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }

        private bool DeleteSingleItemMediaInfo(BaseItem item, DirectoryService directoryService)
        {
            if (!(item is Video))
            {
                return false;
            }

            var workItem = _libraryManager.GetItemById(item.InternalId);
            if (!(workItem is Video video))
            {
                return false;
            }

            Plugin.MediaInfoService.DeleteMediaInfoJson(video, directoryService, "ShortcutMenu");

            _itemRepository.SaveMediaStreams(video.InternalId, new List<MediaStream>(), CancellationToken.None);
            video.MediaStreams = new List<MediaStream>();
            video.RunTimeTicks = null;
            video.TotalBitrate = 0;
            video.Container = null;
            video.Size = 0;
            video.Width = 0;
            video.Height = 0;
            video.UpdateToRepository(ItemUpdateType.MetadataEdit);
            return true;
        }
    }
}
