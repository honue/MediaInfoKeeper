using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Common;
using static MediaInfoKeeper.Options.EnhanceOptions;

namespace MediaInfoKeeper.ScheduledTask
{
    public class MergeMultiVersionTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;

        private static readonly HashSet<string> ProviderIdCheckKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tmdb", "imdb", "tvdb" };

        public static readonly AsyncLocal<CollectionFolder> currentScanLibrary = new AsyncLocal<CollectionFolder>();
        private static int isTriggeredExecutionRunning;
        private static readonly object internalRefreshLock = new object();
        private static readonly HashSet<long> internalRefreshItemIds = new HashSet<long>();

        public MergeMultiVersionTask(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
        }

        public string Key => "MergeMultiVersionTask";

        public string Name => "10.合并多版本";

        public string Description => "按偏好设置合并同一电影/电视剧的多个版本，支持跨媒体库操作。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("MergeMultiVersion - 计划任务执行");

            var currentScanLibraryValue = currentScanLibrary.Value;
            currentScanLibrary.Value = null;

            try
            {
                double cumulativeProgress = 0;

                var seriesLibraryGroups = PrepareMergeSeries();
                var movieLibraryGroups = PrepareMergeMovies(currentScanLibraryValue);

                var processSeries = seriesLibraryGroups.Any() && (currentScanLibraryValue is null ||
                                                                  currentScanLibraryValue.CollectionType == CollectionType.TvShows.ToString() ||
                                                                  currentScanLibraryValue.CollectionType is null);
                var processMovies = movieLibraryGroups.Any() && (currentScanLibraryValue is null ||
                                                                 currentScanLibraryValue.CollectionType == CollectionType.Movies.ToString() ||
                                                                 currentScanLibraryValue.CollectionType is null);

                if (processSeries)
                {
                    var multiply = processMovies ? 1 : 2;

                    var alternativeSeries = FindDuplicateSeries(seriesLibraryGroups);
                    progress.Report(cumulativeProgress += 5.0 * multiply);

                    if (alternativeSeries.Any())
                    {
                        var seriesProgressWeight = 35.0 * multiply / alternativeSeries.Count;

                        foreach (var series in alternativeSeries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var shouldRefresh = currentScanLibraryValue is null ||
                                                this.libraryManager.GetCollectionFolders(series)
                                                    .Any(c => c.InternalId != currentScanLibraryValue.InternalId);
                            await RefreshSeriesAsync(series, cancellationToken, shouldRefresh).ConfigureAwait(false);

                            cumulativeProgress += seriesProgressWeight;
                            progress.Report(cumulativeProgress);
                        }
                    }
                    else
                    {
                        cumulativeProgress += 35.0 * multiply;
                        progress.Report(cumulativeProgress);
                    }

                    var inconsistentSeries = FindInconsistentSeries(seriesLibraryGroups);
                    progress.Report(cumulativeProgress += 5.0 * multiply);

                    if (inconsistentSeries.Any())
                    {
                        var seriesProgressWeight = 5.0 * multiply / inconsistentSeries.Count;

                        foreach (var series in inconsistentSeries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await RefreshSeriesAsync(series, cancellationToken).ConfigureAwait(false);

                            cumulativeProgress += seriesProgressWeight;
                            progress.Report(cumulativeProgress);
                        }
                    }
                    else
                    {
                        cumulativeProgress += 5.0 * multiply;
                        progress.Report(cumulativeProgress);
                    }
                }

                if (processMovies)
                {
                    var totalGroups = movieLibraryGroups.Length;
                    var groupProgressWeight = processSeries ? 50.0 / totalGroups : 100.0 / totalGroups;

                    foreach (var group in movieLibraryGroups)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var groupProgress = new Progress<double>(p =>
                        {
                            cumulativeProgress += p * groupProgressWeight / 100;
                            progress.Report(cumulativeProgress);
                        });

                        ExecuteMergeMovies(group, groupProgress);
                    }
                }

                progress.Report(100);
                this.logger.Info("MergeMultiVersion - 计划任务完成");
            }
            finally
            {
                EndTriggeredExecution();
            }
        }

        public static bool IsInternalRefresh(long itemInternalId)
        {
            lock (internalRefreshLock)
            {
                return internalRefreshItemIds.Remove(itemInternalId);
            }
        }

        public static bool TryBeginTriggeredExecution()
        {
            return Interlocked.CompareExchange(ref isTriggeredExecutionRunning, 1, 0) == 0;
        }

        private long[] PrepareMergeSeries()
        {
            var mergeSeriesPreference = Plugin.Instance.Options.Enhance?.MergeSeriesPreference
                                        ?? MergeSeriesScopeOption.LibraryScope;
            this.logger.Info("MergeMultiVersion - Merge Series Preference: " + mergeSeriesPreference);

            if (mergeSeriesPreference != MergeSeriesScopeOption.GlobalScope)
            {
                return Array.Empty<long>();
            }

            var libraries = Plugin.LibraryService.GetSeriesLibraries()
                .Where(l => l.GetLibraryOptions().EnableAutomaticSeriesGrouping)
                .ToList();

            if (!libraries.Any())
            {
                return Array.Empty<long>();
            }

            this.logger.Info("MergeMultiVersion - Series Libraries: " +
                              string.Join(", ", libraries.Select(l => l.Name)));

            return libraries.Select(l => l.InternalId).ToArray();
        }

        private List<Series> FindDuplicateSeries(long[] parents)
        {
            var allSeries = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = parents,
                IncludeItemTypes = new[] { nameof(Series) },
                HasAnyProviderId = new[]
                {
                    MetadataProviders.Tmdb.ToString(), MetadataProviders.Imdb.ToString(),
                    MetadataProviders.Tvdb.ToString()
                }
            }).ToList();

            var dupSeries = allSeries
                .SelectMany(item =>
                    item.ProviderIds.Where(kvp => ProviderIdCheckKeys.Contains(kvp.Key))
                        .Select(kvp => new { kvp.Key, kvp.Value, item }))
                .GroupBy(x => new { x.Key, x.Value })
                .Where(g =>
                {
                    var uniqueKeys = new HashSet<string>();
                    foreach (var x in g)
                    {
                        uniqueKeys.Add(x.item.PresentationUniqueKey);
                        if (uniqueKeys.Count > 1) return true;
                    }

                    return false;
                })
                .SelectMany(g => g.Select(x => x.item))
                .GroupBy(item => item.InternalId)
                .Select(g => g.First())
                .OfType<Series>()
                .ToList();

            return dupSeries;
        }

        private List<Series> FindInconsistentSeries(long[] parents)
        {
            var allItems = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = parents,
                IncludeItemTypes = new[] { nameof(Season), nameof(Episode) },
                GroupBySeriesPresentationUniqueKey = true
            }).ToList();

            var inconsistentSeries = allItems
                .Select(item => new
                {
                    Series = (item as Season)?.Series ?? (item as Episode)?.Series,
                    item.SeriesPresentationUniqueKey
                })
                .Where(x => x.Series != null && x.SeriesPresentationUniqueKey != x.Series.PresentationUniqueKey)
                .GroupBy(x => x.Series.InternalId)
                .Select(g => g.First().Series)
                .ToList();

            allItems.Clear();

            return inconsistentSeries;
        }

        private long[][] PrepareMergeMovies(CollectionFolder currentScanLibrary)
        {
            var mergeMoviesPreference = Plugin.Instance.Options.Enhance?.MergeMoviesPreference
                                        ?? MergeMoviesScopeOption.FolderScope;
            this.logger.Info("MergeMultiVersion - Merge Movies Preference: " + mergeMoviesPreference);

            var libraryGroups = Array.Empty<long[]>();

            if (mergeMoviesPreference == MergeMoviesScopeOption.FolderScope)
            {
                return libraryGroups;
            }

            if (mergeMoviesPreference == MergeMoviesScopeOption.LibraryScope && currentScanLibrary != null)
            {
                libraryGroups = new[] { new[] { currentScanLibrary.InternalId } };
                this.logger.Info("MergeMultiVersion - Movies Libraries: " + currentScanLibrary.Name);
            }
            else
            {
                var libraries = Plugin.LibraryService.GetMovieLibraries();

                if (!libraries.Any())
                {
                    return libraryGroups;
                }

                this.logger.Info("MergeMultiVersion - Movies Libraries: " +
                                  string.Join(", ", libraries.Select(l => l.Name)));

                var libraryIds = libraries.Select(l => l.InternalId).ToArray();
                libraryGroups = mergeMoviesPreference == MergeMoviesScopeOption.GlobalScope
                    ? new[] { libraryIds }
                    : libraryIds.Select(library => new[] { library }).ToArray();
            }

            return libraryGroups;
        }

        private void ExecuteMergeMovies(long[] parents, IProgress<double> groupProgress = null)
        {
            var allMovies = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = parents,
                IncludeItemTypes = new[] { nameof(Movie) },
                HasAnyProviderId = new[]
                {
                    MetadataProviders.Tmdb.ToString(),
                    MetadataProviders.Imdb.ToString(),
                    MetadataProviders.Tvdb.ToString()
                }
            }).Cast<Movie>().ToList();

            var dupMovies = allMovies
                .SelectMany(item =>
                    item.ProviderIds.Where(kvp => ProviderIdCheckKeys.Contains(kvp.Key))
                        .Select(kvp => new { kvp.Key, kvp.Value, item }))
                .GroupBy(kvp => new { kvp.Key, kvp.Value })
                .Where(g =>
                {
                    var groupItems = g.Select(kvp => kvp.item).ToList();

                    var altVersionCount = g.Sum(kvp =>
                        kvp.item.GetAlternateVersionIds().Count(id =>
                            groupItems.Any(i => i.InternalId == id)));

                    return g.Count() != 1 + altVersionCount / g.Count();
                })
                .ToList();
            allMovies.Clear();

            if (dupMovies.Count > 0)
            {
                var parentMap = new Dictionary<long, long>(dupMovies.Count);

                foreach (var group in dupMovies)
                {
                    long rootId = -1;

                    foreach (var kvp in group)
                    {
                        var movie = kvp.item;

                        if (!parentMap.ContainsKey(movie.InternalId))
                        {
                            parentMap[movie.InternalId] = movie.InternalId;
                        }

                        if (rootId == -1)
                        {
                            rootId = movie.InternalId;
                        }
                        else
                        {
                            UnionFindUtility.Union(rootId, movie.InternalId, parentMap);
                        }
                    }
                }

                var rootIdGroups = parentMap.Values
                    .GroupBy(id => UnionFindUtility.Find(id, parentMap))
                    .ToList();

                var movieLookup = dupMovies.SelectMany(g => g)
                    .GroupBy(kvp => UnionFindUtility.Find(kvp.item.InternalId, parentMap))
                    .ToDictionary(d => d.Key,
                        d => d.GroupBy(kvp => kvp.item.InternalId)
                            .Select(g => g.First().item)
                            .ToList());

                var total = rootIdGroups.Count;
                var current = 0;

                foreach (var group in rootIdGroups)
                {
                    var movies = group
                        .SelectMany(rootId =>
                            movieLookup.TryGetValue(rootId, out var m)
                                ? m
                                : Enumerable.Empty<Movie>())
                        .GroupBy(m => m.InternalId)
                        .Select(g => g.First())
                        .OfType<BaseItem>()
                        .ToArray();

                    this.libraryManager.MergeItems(movies);

                    foreach (var movieItem in movies)
                    {
                        this.logger.Info("MergeMultiVersion - Movie 已合并: {0} - {1}",
                            movieItem.Name, movieItem.Path);
                    }

                    current++;
                    this.logger.Info("MergeMultiVersion - 已合并分组 {0}/{1}，共 {2} 项",
                        current, total, movies.Length);

                    groupProgress?.Report((double)current / total * 100);
                }

                groupProgress?.Report(100);
            }
        }

        private MetadataRefreshOptions GetValidationRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(this.logger, this.fileSystem))
            {
                EnableRemoteContentProbe = false,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        private async Task RefreshSeriesAsync(Series series, CancellationToken cancellationToken, bool shouldRefresh = true)
        {
            if (series == null)
            {
                return;
            }

            if (shouldRefresh)
            {
                var refreshOptions = GetValidationRefreshOptions();
                Traverse.Create(refreshOptions).Property("Recursive").SetValue(true);
                BeginInternalRefresh(series.InternalId);

                try
                {
                    await this.providerManager.RefreshFullItem(series, refreshOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    EndInternalRefresh(series.InternalId);
                }
            }

            this.logger.Info("MergeMultiVersion - Series 已合并: {0} - {1}", series.Name, series.Path);
        }

        private static void BeginInternalRefresh(long itemInternalId)
        {
            lock (internalRefreshLock)
            {
                internalRefreshItemIds.Add(itemInternalId);
            }
        }

        private static void EndInternalRefresh(long itemInternalId)
        {
            lock (internalRefreshLock)
            {
                internalRefreshItemIds.Remove(itemInternalId);
            }
        }

        private static void EndTriggeredExecution()
        {
            Interlocked.Exchange(ref isTriggeredExecutionRunning, 0);
        }
    }
}
