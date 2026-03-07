using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaInfoKeeper.Services.IntroSkip;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class ClearIntroRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public ClearIntroRouteHandler(
            Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems,
            ILibraryManager libraryManager,
            IItemRepository itemRepository)
        {
            _expandToTargetItems = expandToTargetItems;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public MediaInfoMenuResponse Handle(ClearIntroRequest request)
        {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "no items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu ClearIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids).OfType<BaseItem>().ToList();
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "no supported items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu ClearIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var introSkipApi = new IntroSkipChapterApi(_libraryManager, _itemRepository, Plugin.Instance.Logger);

            foreach (var item in targetItems)
            {
                response.Processed++;
                try
                {
                    introSkipApi.RemoveIntroMarkers(item);
                    response.Succeeded++;
                    Plugin.Instance.Logger.Info($"ShortcutMenu 清除片头成功: {item.Path ?? item.Name}");
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单清除片头失败: {item.Path ?? item.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = response.Succeeded > 0 ? "clear intro succeeded" : "no intro cleared";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu ClearIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }
    }
}