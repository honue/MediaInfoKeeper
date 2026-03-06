using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Services;
using MediaInfoKeeper.Web.Handler;

namespace MediaInfoKeeper.Web
{
    [Unauthenticated]
    public class ShortcutMenuService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory _resultFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly ExtractMediaInfoRouteHandler _extractHandler;
        private readonly DeleteMediaInfoPersistRouteHandler _deletePersistHandler;
        private readonly ScanIntroRouteHandler _scanIntroHandler;
        private readonly SetIntroRouteHandler _setIntroHandler;
        private readonly ClearIntroRouteHandler _clearIntroHandler;

        public ShortcutMenuService(
            IHttpResultFactory resultFactory,
            ILibraryManager libraryManager,
            IItemRepository itemRepository)
        {
            _resultFactory = resultFactory;
            _libraryManager = libraryManager;
            _extractHandler = new ExtractMediaInfoRouteHandler(ExpandToTargetItems);
            _deletePersistHandler = new DeleteMediaInfoPersistRouteHandler(ExpandToTargetItems, libraryManager, itemRepository);
            _scanIntroHandler = new ScanIntroRouteHandler(ExpandToTargetItems);
            _setIntroHandler = new SetIntroRouteHandler(ExpandToTargetItems, libraryManager, itemRepository);
            _clearIntroHandler = new ClearIntroRouteHandler(ExpandToTargetItems, libraryManager, itemRepository);
        }

        public IRequest Request { get; set; }

        public object Get(MediaInfoKeeperJsRequest request)
        {
            return _resultFactory.GetResult(Request,
                (ReadOnlyMemory<byte>)ShortcutMenuLoader.MediaInfoKeeperJs.GetBuffer(), "application/x-javascript");
        }

        public object Get(ShortcutMenuRequest request)
        {
            return _resultFactory.GetResult(ShortcutMenuLoader.ModifiedShortcutsString.AsSpan(),
                "application/x-javascript");
        }

        public MediaInfoMenuResponse Post(ExtractMediaInfoRequest request)
        {
            return _extractHandler.HandleAsync(request).GetAwaiter().GetResult();
        }

        public MediaInfoMenuResponse Post(DeleteMediaInfoPersistRequest request)
        {
            return _deletePersistHandler.Handle(request);
        }

        public MediaInfoMenuResponse Post(ScanIntroRequest request)
        {
            return _scanIntroHandler.Handle(request);
        }

        public MediaInfoMenuResponse Post(SetIntroRequest request)
        {
            return _setIntroHandler.Handle(request);
        }

        public MediaInfoMenuResponse Post(ClearIntroRequest request)
        {
            return _clearIntroHandler.Handle(request);
        }

        private List<BaseItem> ExpandToTargetItems(IEnumerable<string> ids)
        {
            var targets = new List<BaseItem>();
            var known = new HashSet<long>();

            foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)))
            {
                var item = _libraryManager.GetItemById(id);
                if (item == null)
                {
                    continue;
                }

                if (item is Episode episode)
                {
                    if (episode.ExtraType == null && known.Add(episode.InternalId))
                    {
                        targets.Add(episode);
                    }

                    continue;
                }

                if (item is Video video)
                {
                    if (video.ExtraType == null && known.Add(video.InternalId))
                    {
                        targets.Add(video);
                    }

                    continue;
                }

                if (!(item is Series || item is Season))
                {
                    continue;
                }

                var episodes = Plugin.LibraryService.GetSeriesEpisodesFromItem(item);
                foreach (var episodeItem in episodes)
                {
                    if (episodeItem?.ExtraType == null && known.Add(episodeItem.InternalId))
                    {
                        targets.Add(episodeItem);
                    }
                }
            }

            return targets;
        }
    }
}