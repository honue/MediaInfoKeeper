using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaInfoKeeper.Configuration;

namespace MediaInfoKeeper.Services
{
    internal static class SearchScopeUtility
    {
        private static string[] includeItemTypes = Array.Empty<string>();

        public static void UpdateSearchScope(string currentScope)
        {
            var searchScope = currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                              Array.Empty<string>();

            var includeTypes = new List<string>();
            foreach (var scope in searchScope)
            {
                if (Enum.TryParse(scope, true, out EnhanceChineseSearchOptions.SearchItemType type))
                {
                    switch (type)
                    {
                        case EnhanceChineseSearchOptions.SearchItemType.Collection:
                            includeTypes.AddRange(new[] { nameof(BoxSet) });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.Episode:
                            includeTypes.AddRange(new[] { nameof(Episode) });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.LiveTv:
                            includeTypes.AddRange(new[] { nameof(LiveTvChannel), nameof(LiveTvProgram), "LiveTVSeries" });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.Movie:
                            includeTypes.AddRange(new[] { nameof(Movie) });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.Person:
                            includeTypes.AddRange(new[] { nameof(Person) });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.Playlist:
                            includeTypes.AddRange(new[] { nameof(Playlist) });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.Series:
                            includeTypes.AddRange(new[] { nameof(Series) });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.Season:
                            includeTypes.AddRange(new[] { nameof(Season) });
                            break;
                        case EnhanceChineseSearchOptions.SearchItemType.Video:
                            includeTypes.AddRange(new[] { nameof(Video) });
                            break;
                    }
                }
            }

            includeItemTypes = includeTypes.ToArray();
        }

        public static string[] GetSearchScope()
        {
            return includeItemTypes;
        }
    }
}
