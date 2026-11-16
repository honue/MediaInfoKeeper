using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.Patch;
using MediaInfoKeeper.Store;

namespace MediaInfoKeeper.Web.Handler {
    internal sealed class DeleteMediaInfoPersistRouteHandler {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;
        private readonly IItemRepository _itemRepository;
        private readonly ILibraryManager _libraryManager;

        public DeleteMediaInfoPersistRouteHandler(
            Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems,
            ILibraryManager libraryManager,
            IItemRepository itemRepository) {
            _expandToTargetItems = expandToTargetItems;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public MediaInfoMenuResponse Handle(DeleteMediaInfoPersistRequest request) {
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

            var targetItems = _expandToTargetItems(request.Ids);
            response.Total = targetItems.Count;

            if (targetItems.Count == 0) {
                response.Message = "no supported items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu DeleteMediaInfoPersist result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            foreach (var item in targetItems) {
                response.Processed++;
                try {
                    if (DeleteSingleItemMediaInfo(item))
                        response.Succeeded++;
                    else
                        response.Skipped++;
                }
                catch (Exception ex) {
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

        private bool DeleteSingleItemMediaInfo(BaseItem item) {
            if (!(item is Video) && !(item is Audio)) return false;

            var workItem = _libraryManager.GetItemById(item.InternalId);
            if (!(workItem is Video) && !(workItem is Audio)) return false;

            _itemRepository.SaveMediaStreams(workItem.InternalId, new List<MediaStream>(), CancellationToken.None);

            workItem.MediaStreams = new List<MediaStream>();
            workItem.RunTimeTicks = null;
            workItem.TotalBitrate = 0;
            workItem.Container = null;
            workItem.Size = 0;
            workItem.UpdateToRepository(ItemUpdateType.MetadataEdit);

            MediaInfoDocument.DeleteMediaInfoJson(
                workItem,
                new DirectoryService(Plugin.SharedLogger, Plugin.FileSystem),
                "快捷菜单");
            return true;
        }
    }
}
