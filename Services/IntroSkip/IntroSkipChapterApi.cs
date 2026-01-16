using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MediaInfoKeeper.Services.IntroSkip
{
    public class IntroSkipChapterApi
    {
        private const string MarkerSuffix = "#MIK";

        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IItemRepository itemRepository;

        public IntroSkipChapterApi(ILibraryManager libraryManager, IItemRepository itemRepository, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.itemRepository = itemRepository;
            this.logger = logger;
        }

        public List<ChapterInfo> GetChapters(BaseItem item)
        {
            return itemRepository.GetChapters(item);
        }

        public long? GetIntroStart(BaseItem item)
        {
            var introStart = itemRepository.GetChapters(item)
                .FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
            return introStart?.StartPositionTicks;
        }

        public long? GetIntroEnd(BaseItem item)
        {
            var introEnd = itemRepository.GetChapters(item)
                .FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);
            return introEnd?.StartPositionTicks;
        }

        public long? GetCreditsStart(BaseItem item)
        {
            var creditsStart = itemRepository.GetChapters(item)
                .FirstOrDefault(c => c.MarkerType == MarkerType.CreditsStart);
            return creditsStart?.StartPositionTicks;
        }

        public void UpdateIntro(Episode item, SessionInfo session, long introStartPositionTicks,
            long introEndPositionTicks)
        {
            if (introStartPositionTicks > introEndPositionTicks)
            {
                return;
            }

            var resultEpisodes = FetchEpisodes(item);

            foreach (var episode in resultEpisodes)
            {
                var chapters = itemRepository.GetChapters(episode);

                if (HasNonMikIntro(chapters))
                {
                    continue;
                }

                chapters.RemoveAll(c => c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroStart + MarkerSuffix,
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = introStartPositionTicks
                });
                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroEnd + MarkerSuffix,
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = introEndPositionTicks
                });

                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                using (IntroMarkerProtect.AllowSave(episode.InternalId))
                {
                    itemRepository.SaveChapters(episode.InternalId, chapters);
                }
            }

            logger.Info("片头标记已更新，用户: " + session.UserName + "，条目: " +
                        (item.Path ?? item.Name));
        }

        public void UpdateCredits(Episode item, SessionInfo session, long creditsDurationTicks)
        {
            var resultEpisodes = FetchEpisodes(item);

            foreach (var episode in resultEpisodes)
            {
                if (!episode.RunTimeTicks.HasValue)
                {
                    continue;
                }

                if (episode.RunTimeTicks.Value - creditsDurationTicks <= 0)
                {
                    continue;
                }

                var chapters = itemRepository.GetChapters(episode);

                if (HasNonMikCredits(chapters))
                {
                    continue;
                }

                chapters.RemoveAll(c => c.MarkerType == MarkerType.CreditsStart);

                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.CreditsStart + MarkerSuffix,
                    MarkerType = MarkerType.CreditsStart,
                    StartPositionTicks = episode.RunTimeTicks.Value - creditsDurationTicks
                });

                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                using (IntroMarkerProtect.AllowSave(episode.InternalId))
                {
                    itemRepository.SaveChapters(episode.InternalId, chapters);
                }
            }

            logger.Info("片尾标记已更新，用户: " + session.UserName + "，条目: " +
                        (item.Path ?? item.Name));
        }

        private static bool IsMarkerAddedByMik(ChapterInfo chapter)
        {
            return chapter.Name?.EndsWith(MarkerSuffix, StringComparison.Ordinal) == true;
        }

        private static bool HasNonMikIntro(List<ChapterInfo> chapters)
        {
            return chapters.Any(c => (c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd) &&
                                     !IsMarkerAddedByMik(c));
        }

        private static bool HasNonMikCredits(List<ChapterInfo> chapters)
        {
            return chapters.Any(c => c.MarkerType == MarkerType.CreditsStart && !IsMarkerAddedByMik(c));
        }

        private List<Episode> FetchEpisodes(BaseItem item)
        {
            var episodesQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                ParentIds = new[] { item.ParentId }
            };

            var episodes = libraryManager.GetItemList(episodesQuery)
                .OfType<Episode>()
                .ToList();

            return episodes;
        }
    }
}
