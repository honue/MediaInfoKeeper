using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaInfoKeeper.Web;
using MediaInfoKeeper.Services.IntroSkip;

namespace MediaInfoKeeper.Web.Handler
{
    internal sealed class SetIntroRouteHandler
    {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public SetIntroRouteHandler(
            Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems,
            ILibraryManager libraryManager,
            IItemRepository itemRepository)
        {
            _expandToTargetItems = expandToTargetItems;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public MediaInfoMenuResponse Handle(SetIntroRequest request)
        {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0)
            {
                response.Message = "no items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var targetItems = _expandToTargetItems(request.Ids).OfType<Episode>().ToList();
            response.Total = targetItems.Count;

            if (targetItems.Count == 0)
            {
                response.Message = "no supported items";
                Plugin.Instance.Logger.Info(
                    $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
                return response;
            }

            var introSkipApi = new IntroSkipChapterApi(_libraryManager, _itemRepository, Plugin.Instance.Logger);

            foreach (var episode in targetItems)
            {
                response.Processed++;
                try
                {
                    var chapters = _itemRepository.GetChapters(episode);

                    chapters.RemoveAll(c => 
                        (c.MarkerType == MediaBrowser.Model.Entities.MarkerType.IntroStart || 
                         c.MarkerType == MediaBrowser.Model.Entities.MarkerType.IntroEnd) &&
                        c.Name?.EndsWith("#MIK") == true);

                    chapters.Add(new MediaBrowser.Model.Entities.ChapterInfo
                    {
                        Name = "片头",
                        MarkerType = MediaBrowser.Model.Entities.MarkerType.IntroStart,
                        StartPositionTicks = request.IntroStartTicks
                    });
                    chapters.Add(new MediaBrowser.Model.Entities.ChapterInfo
                    {
                        Name = "片尾",
                        MarkerType = MediaBrowser.Model.Entities.MarkerType.IntroEnd,
                        StartPositionTicks = request.IntroEndTicks
                    });

                    chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                    using (Patch.IntroMarkerProtect.AllowSave(episode.InternalId))
                    {
                        _itemRepository.SaveChapters(episode.InternalId, chapters);
                    }

                    response.Succeeded++;
                    Plugin.Instance.Logger.Info($"ShortcutMenu 设置片头成功: {episode.Path ?? episode.Name}, Start={request.IntroStartTicks}, End={request.IntroEndTicks}");
                }
                catch (Exception ex)
                {
                    response.Failed++;
                    Plugin.Instance.Logger.Error($"快捷菜单设置片头失败: {episode.Path ?? episode.Name}");
                    Plugin.Instance.Logger.Error(ex.Message);
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
            }

            response.Message = response.Succeeded > 0 ? "set intro succeeded" : "no intro set";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu SetIntro result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }
    }
}