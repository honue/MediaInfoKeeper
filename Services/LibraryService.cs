using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaInfoKeeper.Common;
using Microsoft.Extensions.Caching.Memory;

namespace MediaInfoKeeper.Services {
    public class LibraryService {
        private static readonly TimeSpan FavoriteUsersCacheTtl = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan EpisodeCountCacheTtl = TimeSpan.FromSeconds(30);

        private static readonly MemoryCacheEntryOptions FavoriteUsersCacheEntryOptions = new() {
            SlidingExpiration = FavoriteUsersCacheTtl
        };

        private static readonly MemoryCacheEntryOptions EpisodeCountCacheEntryOptions = new() {
            AbsoluteExpirationRelativeToNow = EpisodeCountCacheTtl
        };

        private readonly MemoryCache favoriteUsersBySeriesIdCache = new(new MemoryCacheOptions());
        private readonly MemoryCache episodeCountByItemIdCache = new(new MemoryCacheOptions());
        private readonly IFileSystem fileSystem;
        private readonly ILibraryManager libraryManager;
        private readonly ILogger logger;
        private readonly IMediaMountManager mediaMountManager;
        private readonly IUserDataManager userDataManager;
        private readonly IUserManager userManager;
        private string[] adminOrderedViews = Array.Empty<string>();
        private Dictionary<User, bool> allUsers = new();

        /// <summary>创建库处理辅助类并注入所需服务。</summary>
        public LibraryService(
            ILibraryManager libraryManager,
            IFileSystem fileSystem,
            IUserManager userManager,
            IUserDataManager userDataManager,
            IMediaMountManager mediaMountManager) {
            logger = Plugin.SharedLogger;
            this.libraryManager = libraryManager;
            this.fileSystem = fileSystem;
            this.userManager = userManager;
            this.userDataManager = userDataManager;
            this.mediaMountManager = mediaMountManager;
            RefreshUsers();
        }

        /// <summary>构建媒体库选择列表，用于配置 UI 复用。</summary>
        public List<EditorSelectOption> BuildLibrarySelectOptions() {
            var list = new List<EditorSelectOption>();
            foreach (var folder in libraryManager.GetVirtualFolders()) {
                if (folder == null) continue;

                var name = string.IsNullOrWhiteSpace(folder.Name) ? folder.ItemId : folder.Name;
                list.Add(new EditorSelectOption {
                    Value = folder.ItemId,
                    Name = name,
                    IsEnabled = true
                });
            }

            return list;
        }

        /// <summary>判断条目是否执行过刷新。</summary>
        public bool IsItemRefreshedRecently(BaseItem item) {
            if (item == null)
                return false;

            var last = item.DateLastRefreshed;

            // 从未刷新过
            if (last == DateTimeOffset.MinValue)
                return false;

            return last >= ConfiguredDateTime.NowOffset.AddMinutes(-10);
        }

        /// <summary>判断条目是否已有封面；音乐条目会同时检查展示父级（如专辑）主图。</summary>
        public bool HasCover(BaseItem item) {
            if (item == null) return false;

            if (item.HasImage(ImageType.Primary)) return true;

            var displayParentId = item.ImageDisplayParentId;
            if (displayParentId == 0 || displayParentId == item.InternalId) return false;

            var displayParent = libraryManager.GetItemById(displayParentId);
            return displayParent?.HasImage(ImageType.Primary) == true;
        }

        private IReadOnlyCollection<User> GetAllUsers(bool refresh = true) {
            if (refresh) RefreshUsers();

            return allUsers.Keys.ToList();
        }

        public string[] GetAdminOrderedViews() {
            RefreshUsers();
            return adminOrderedViews;
        }

        /// <summary>根据配置生成媒体库路径列表。</summary>
        public List<string> GetScopedLibraryPaths(string scopedLibraries, out bool hasScope) {
            var tokens = ParseScopedLibraryTokens(scopedLibraries);
            hasScope = tokens.Count > 0;

            var libraries = libraryManager.GetVirtualFolders();
            if (tokens.Count > 0)
                libraries = libraries
                    .Where(folder =>
                        (!string.IsNullOrWhiteSpace(folder.ItemId) && tokens.Contains(folder.ItemId)) ||
                        (!string.IsNullOrWhiteSpace(folder.Name) && tokens.Contains(folder.Name.Trim())))
                    .ToList();

            return NormalizeLibraryPaths(libraries.SelectMany(folder => folder.Locations ?? Array.Empty<string>()));
        }

        public List<CollectionFolder> GetMovieLibraries() {
            var libraries = libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.Movies.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public List<CollectionFolder> GetSeriesLibraries() {
            var libraries = libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.TvShows.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public void EnsureLibraryEnabledAutomaticSeriesGrouping() {
            var libraries = libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries) {
                var options = library.LibraryOptions;

                if (!options.EnableAutomaticSeriesGrouping && long.TryParse(library.ItemId, out var itemId)) {
                    options.EnableAutomaticSeriesGrouping = true;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        /// <summary>获取所有媒体库的物理根路径。</summary>
        public List<string> GetAllLibraryPaths(bool existingOnly = true) {
            var paths = NormalizeLibraryPaths(
                libraryManager.GetVirtualFolders()
                    .Where(folder => folder?.Locations != null)
                    .SelectMany(folder => folder.Locations ?? Array.Empty<string>()));

            if (!existingOnly) return paths;

            return paths
                .Where(path => Directory.Exists(path))
                .ToList();
        }

        public async Task<string> GetStrmMountPathAsync(string strmPath) {
            if (string.IsNullOrWhiteSpace(strmPath)) return null;

            if (mediaMountManager == null) {
                logger.Warn("MediaMountManager 为空，无法解析 strm 挂载路径");
                return null;
            }

            try {
                using var mediaMount = await mediaMountManager.Mount(strmPath, null, CancellationToken.None)
                    .ConfigureAwait(false);
                return mediaMount?.MountedPathInfo?.FullName;
            }
            catch (Exception ex) {
                logger.Warn($"strm 挂载路径解析异常 {strmPath}");
                logger.Warn(ex.Message);
                return null;
            }
        }

        /// <summary>按路径范围获取音视频条目。</summary>
        public List<BaseItem> FetchScopedVideoItems(IReadOnlyCollection<string> scopePaths,
            bool orderByDateCreatedDesc = false, int? take = null, bool includeAudio = false) {
            var query = new InternalItemsQuery {
                Recursive = true,
                HasPath = true,
                MediaTypes = includeAudio ? new[] { MediaType.Video, MediaType.Audio } : new[] { MediaType.Video }
            };

            if (scopePaths != null && scopePaths.Count > 0) query.PathStartsWithAny = scopePaths.ToArray();

            var items = libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null);

            if (orderByDateCreatedDesc) items = items.OrderByDescending(i => i.DateCreated);

            if (take.HasValue) items = items.Take(take.Value);

            return items.ToList();
        }

        public List<BaseItem> ExpandItem(IEnumerable<string> ids) {
            var targets = new List<BaseItem>();
            var known = new HashSet<long>();

            foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i))) {
                var item = libraryManager.GetItemById(id);
                if (item == null) continue;

                if (item is Episode episode) {
                    if (episode.ExtraType == null && known.Add(episode.InternalId)) targets.Add(episode);

                    continue;
                }

                if (item is Video video) {
                    if (video.ExtraType == null && known.Add(video.InternalId)) targets.Add(video);

                    continue;
                }

                if (item is Audio audio) {
                    if (audio.ExtraType == null && known.Add(audio.InternalId)) targets.Add(audio);

                    continue;
                }

                if (item is MusicAlbum || item is MusicArtist || item is MusicGenre) {
                    foreach (var audioItem in ExpandToAudioItems(item))
                        if (audioItem.ExtraType == null && known.Add(audioItem.InternalId))
                            targets.Add(audioItem);

                    continue;
                }

                if (item is CollectionFolder collectionFolder) {
                    foreach (var mediaItem in ExpandCollectionFolder(collectionFolder))
                        if (mediaItem.ExtraType == null && known.Add(mediaItem.InternalId))
                            targets.Add(mediaItem);

                    continue;
                }

                if (!(item is Series || item is Season)) continue;

                var episodes = GetSeriesEpisodesFromItem(item);
                foreach (var episodeItem in episodes)
                    if (episodeItem?.ExtraType == null && known.Add(episodeItem.InternalId))
                        targets.Add(episodeItem);
            }

            return targets;
        }

        private List<BaseItem> ExpandCollectionFolder(CollectionFolder collectionFolder) {
            if (collectionFolder == null) return new List<BaseItem>();

            var paths = GetScopedLibraryPaths(collectionFolder.InternalId.ToString(), out _);
            if (paths.Count == 0) return new List<BaseItem>();

            return FetchScopedVideoItems(paths, includeAudio: true);
        }

        private IEnumerable<Audio> ExpandToAudioItems(BaseItem item) {
            if (item == null) return Array.Empty<Audio>();

            return libraryManager.GetItemList(new InternalItemsQuery {
                    Recursive = true,
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Audio },
                    IncludeItemTypes = new[] { nameof(Audio) },
                    ParentIds = new[] { item.InternalId }
                })
                .OfType<Audio>();
        }

        /// <summary>获取计划任务媒体库范围内的音视频条目。</summary>
        public List<BaseItem> FetchScheduledTaskLibraryItems(
            string taskScopedLibraries = null,
            bool orderByDateCreatedDesc = false,
            int? take = null,
            bool includeAudio = false) {
            var scopePaths = GetScopedLibraryPaths(taskScopedLibraries ?? string.Empty, out var hasScope);
            if (hasScope && scopePaths.Count == 0) return new List<BaseItem>();

            return FetchScopedVideoItems(scopePaths, orderByDateCreatedDesc, take, includeAudio);
        }

        /// <summary>获取计划任务媒体库范围内、命中最近时间窗口的音视频条目。</summary>
        public List<BaseItem> FetchRecentScheduledTaskLibraryItems(
            DateTime? cutoff,
            string taskScopedLibraries = null,
            bool orderByDateCreatedDesc = true,
            int? take = null,
            bool includeAudio = false) {
            return FetchScheduledTaskLibraryItems(taskScopedLibraries, orderByDateCreatedDesc, take, includeAudio)
                .Where(i => cutoff == null || i.DateCreated >= cutoff)
                .ToList();
        }

        /// <summary>按时间窗口获取最近条目。</summary>
        public List<BaseItem> FetchRecentItems(DateTime? cutoff, bool orderByDateCreatedDesc = true,
            bool includeAudio = false) {
            var query = new InternalItemsQuery {
                Recursive = true,
                HasPath = true,
                MediaTypes = includeAudio ? new[] { MediaType.Video, MediaType.Audio } : new[] { MediaType.Video }
            };

            var items = libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null)
                .Where(i => cutoff == null || i.DateCreated >= cutoff);

            if (orderByDateCreatedDesc) items = items.OrderByDescending(i => i.DateCreated);

            return items.ToList();
        }

        /// <summary>按时间倒序获取最近的剧集条目。</summary>
        public List<Episode> FetchRecentItems(int limit) {
            var query = new InternalItemsQuery {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var items = libraryManager.GetItemList(query)
                .OfType<Episode>()
                .Where(i => i.ExtraType is null)
                .OrderByDescending(i => i.DateCreated)
                .Take(Math.Max(1, limit))
                .ToList();

            return items;
        }

        /// <summary>获取全局收藏的视频条目。</summary>
        public List<BaseItem> FetchFavoriteVideoItems() {
            var query = new InternalItemsQuery {
                Recursive = true,
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            };

            var items = libraryManager.GetItemList(query)
                .Where(i => i.ExtraType is null)
                .Where(IsFavoriteByAnyUser)
                .ToList();

            return items;
        }

        public IReadOnlyList<Episode> FetchSeriesEpisodes(Series series) {
            if (series == null) return Array.Empty<Episode>();

            var episodes = new List<Episode>();
            var known = new HashSet<long>();

            var rootEpisodes = libraryManager.GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    ParentIds = new[] { series.InternalId }
                })
                .OfType<Episode>();

            foreach (var episode in rootEpisodes)
                if (known.Add(episode.InternalId))
                    episodes.Add(episode);

            var seasons = libraryManager.GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = new[] { nameof(Season) },
                    ParentIds = new[] { series.InternalId }
                })
                .OfType<Season>();

            foreach (var season in seasons) {
                var seasonEpisodes = libraryManager.GetItemList(new InternalItemsQuery {
                        IncludeItemTypes = new[] { nameof(Episode) },
                        HasPath = true,
                        MediaTypes = new[] { MediaType.Video },
                        ParentIds = new[] { season.InternalId }
                    })
                    .OfType<Episode>();

                foreach (var episode in seasonEpisodes)
                    if (known.Add(episode.InternalId))
                        episodes.Add(episode);
            }

            return episodes;
        }

        /// <summary>
        ///     获取电视剧或季度的总集数。使用 Emby 的零条目计数查询，避免为 DTO 展示逐季加载完整 Episode 实体。
        /// </summary>
        public int? GetEpisodeCount(BaseItem item, CancellationToken cancellationToken = default) {
            if (item is not Series && item is not Season) return null;

            if (episodeCountByItemIdCache.TryGetValue(item.InternalId, out int cachedCount))
                return cachedCount;

            var query = new InternalItemsQuery {
                Recursive = true,
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video },
                Limit = 0,
                EnableTotalRecordCount = true
            };

            var count = item switch {
                Series series => series.GetItems(query, cancellationToken).TotalRecordCount,
                Season season => season.GetEpisodes(query, cancellationToken).TotalRecordCount,
                _ => 0
            };

            episodeCountByItemIdCache.Set(item.InternalId, count, EpisodeCountCacheEntryOptions);
            return count;
        }

        // 如果是 Series 就返回全剧集；如果是 Episode 或 Season 就只返回该季的所有剧集
        public IReadOnlyList<Episode> GetSeriesEpisodesFromItem(BaseItem item) {
            if (item is Series series) return FetchSeriesEpisodes(series);

            if (item is Episode episode) {
                var seasonFromEpisode = libraryManager.GetItemById(episode.ParentId) as Season;
                return seasonFromEpisode != null
                    ? FetchSeasonEpisodes(seasonFromEpisode)
                    : Array.Empty<Episode>();
            }

            if (item is Season season) return FetchSeasonEpisodes(season);

            return Array.Empty<Episode>();
        }

        public Series GetSeries(long seriesId) {
            return seriesId == 0
                ? null
                : libraryManager.GetItemById(seriesId) as Series;
        }

        public IReadOnlyList<long> NextEpisodesId(Episode episode, int count = 0) {
            if (episode == null) return Array.Empty<long>();

            var season = libraryManager.GetItemById(episode.ParentId) as Season;
            if (season == null) return Array.Empty<long>();

            var orderedEpisodes = FetchSeasonEpisodes(season)
                .Where(i => i?.ExtraType == null)
                .OrderBy(i => i.IndexNumber ?? int.MaxValue)
                .ThenBy(i => i.DateCreated)
                .ToList();

            for (var index = 0; index < orderedEpisodes.Count; index++) {
                if (orderedEpisodes[index].InternalId != episode.InternalId) continue;

                var nextIds = orderedEpisodes
                    .Skip(index + 1)
                    .Select(i => i.InternalId);

                if (count > 0) nextIds = nextIds.Take(count);

                return nextIds.ToList();
            }

            return Array.Empty<long>();
        }

        private IReadOnlyList<Episode> FetchSeasonEpisodes(Season season) {
            if (season == null) return Array.Empty<Episode>();

            return libraryManager.GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video },
                    ParentIds = new[] { season.InternalId }
                })
                .OfType<Episode>()
                .ToList();
        }

        public bool IsFavoriteByAnyUser(BaseItem item) {
            var userDataList = userDataManager.GetAllUserData(item.InternalId);
            if (userDataList == null || userDataList.Count == 0) return false;

            return userDataList.Any(data => data?.IsFavorite == true);
        }

        public bool IsSeriesFavoriteByAnyUser(Series series) {
            if (series == null) return false;

            if (IsFavoriteByAnyUser(series)) return true;

            var seasons = libraryManager.GetItemList(new InternalItemsQuery {
                IncludeItemTypes = new[] { nameof(Season) },
                ParentIds = new[] { series.InternalId }
            }).OfType<Season>();

            foreach (var season in seasons)
                if (IsFavoriteByAnyUser(season))
                    return true;

            return false;
        }

        public IReadOnlyList<User> GetFavoriteUsersBySeriesId(long seriesId) {
            if (seriesId == 0) return Array.Empty<User>();

            var cacheKey = $"favorite-users-series:{seriesId}";
            if (favoriteUsersBySeriesIdCache.TryGetValue(cacheKey, out List<User> cachedUsers)) {
                if (cachedUsers == null) return Array.Empty<User>();

                return cachedUsers.ToList();
            }

            var series = libraryManager.GetItemById(seriesId) as Series;
            if (series == null) return Array.Empty<User>();

            var seasons = libraryManager.GetItemList(new InternalItemsQuery {
                IncludeItemTypes = new[] { nameof(Season) },
                ParentIds = new[] { seriesId }
            }).OfType<Season>().ToList();

            var users = GetAllUsers();
            var matched = new List<User>();

            foreach (var user in users) {
                if (userDataManager.GetUserData(user, series)?.IsFavorite == true) {
                    matched.Add(user);
                    continue;
                }

                if (seasons.Any(season => userDataManager.GetUserData(user, season)?.IsFavorite == true)) matched.Add(user);
            }

            favoriteUsersBySeriesIdCache.Set(cacheKey, matched.ToList(), FavoriteUsersCacheEntryOptions);

            return matched;
        }

        private static HashSet<string> ParseScopedLibraryTokens(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var tokens = raw
                .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrEmpty(value));

            return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> NormalizeLibraryPaths(IEnumerable<string> paths) {
            var separator = Path.DirectorySeparatorChar.ToString();
            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.EndsWith(separator, StringComparison.Ordinal)
                    ? path
                    : path + separator)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RefreshUsers() {
            if (userManager == null) {
                allUsers = new Dictionary<User, bool>();
                adminOrderedViews = Array.Empty<string>();
                return;
            }

            try {
                var users = userManager.GetUserList(new UserQuery()) ?? Array.Empty<User>();
                allUsers = users.ToDictionary(u => u, u => u.Policy?.IsAdministrator == true);
                adminOrderedViews = users.FirstOrDefault(u => u.Policy?.IsAdministrator == true)
                    ?.Configuration?.OrderedViews ?? Array.Empty<string>();
            }
            catch {
                allUsers = new Dictionary<User, bool>();
                adminOrderedViews = Array.Empty<string>();
            }
        }

        public Dictionary<string, bool> PrepareDeepDelete(BaseItem item, string[] scope = null) {
            var deleteItems = new List<BaseItem> { item };

            if (item is Folder folder)
                deleteItems.AddRange(folder.GetItemList(new InternalItemsQuery {
                    Recursive = true,
                    ForceOriginalFolders = item is Playlist || item is BoxSet
                }));

            deleteItems = deleteItems.Where(i => i is IHasMediaSources).ToList();

            var mountPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var single = scope is null;
            var totalMediaSources = 0;
            var shortcutSources = 0;
            var symlinkSources = 0;
            var staticSourceMiss = 0;
            var classifyFail = 0;
            var duplicateSkip = 0;
            var addedLocal = 0;
            var addedRemote = 0;

            foreach (var workItem in deleteItems) {
                var mediaSources =
                    workItem.GetMediaSources(!single, false, libraryManager.GetLibraryOptions(workItem));
                mediaSources = mediaSources.Where(s =>
                        single || scope?.Any(p => s.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is true)
                    .ToList();
                totalMediaSources += mediaSources.Count;

                var staticMediaSources = Plugin.MediaInfoService.GetStaticMediaSources(workItem, !single)
                    .ToDictionary(s => s.Id, s => s.Path);

                foreach (var source in mediaSources)
                    if (IsFileShortcut(source.Path)) {
                        shortcutSources++;
                        var added = false;
                        var hasStaticMountPath = staticMediaSources.TryGetValue(source.Id, out var mountPath);
                        var staticFailReason = string.Empty;

                        if (hasStaticMountPath &&
                            TryClassifyMountPath(mountPath, out var pathKey, out var isLocal) &&
                            !IsFileShortcut(pathKey)) {
                            if (!mountPaths.ContainsKey(pathKey)) {
                                mountPaths.Add(pathKey, isLocal);
                                if (isLocal)
                                    addedLocal++;
                                else
                                    addedRemote++;
                            }
                            else {
                                duplicateSkip++;
                            }

                            added = true;
                        }
                        else {
                            if (!hasStaticMountPath) {
                                staticSourceMiss++;
                                staticFailReason = "staticMountPathMissing";
                            }
                            else if (string.IsNullOrWhiteSpace(mountPath)) {
                                staticFailReason = "empty";
                            }
                            else if (!TryClassifyMountPath(mountPath, out pathKey, out isLocal)) {
                                staticFailReason = "invalid";
                            }
                            else if (IsFileShortcut(pathKey)) {
                                staticFailReason = "isShortcut";
                            }
                            else {
                                staticFailReason = "unknown";
                            }
                        }

                        if (!added) {
                            if (TryResolveStrmTargetPath(source.Path, out var parsedMountPath, out var parseReason) &&
                                TryClassifyMountPath(parsedMountPath, out var parsedPathKey, out var parsedIsLocal) &&
                                !IsFileShortcut(parsedPathKey)) {
                                if (!mountPaths.ContainsKey(parsedPathKey)) {
                                    mountPaths.Add(parsedPathKey, parsedIsLocal);
                                    if (parsedIsLocal)
                                        addedLocal++;
                                    else
                                        addedRemote++;

                                    logger.Debug(
                                        "DeepDelete - STRM 兜底解析成功: 源路径={0}, 挂载路径={1}",
                                        source.Path, parsedMountPath);
                                }
                                else {
                                    duplicateSkip++;
                                }
                            }
                            else {
                                classifyFail++;
                                logger.Debug(
                                    "DeepDelete - STRM 解析失败: 源路径={0}, 挂载路径={1}, reason={2}, 兜底路径={3}, fallbackReason={4}",
                                    source.Path,
                                    mountPath ?? "<null>",
                                    staticFailReason,
                                    parsedMountPath ?? "<null>",
                                    parseReason ?? "<null>");
                            }
                        }
                    }
                    else if (IsSymlink(source.Path)) {
                        symlinkSources++;
                        var targetPath = GetSymlinkTarget(source.Path);

                        if (TryClassifyMountPath(targetPath, out var pathKey, out var isLocal) && isLocal) {
                            if (!mountPaths.ContainsKey(pathKey)) {
                                mountPaths.Add(pathKey, true);
                                addedLocal++;
                            }
                            else {
                                duplicateSkip++;
                            }
                        }
                        else {
                            classifyFail++;
                        }
                    }
            }

            logger.Debug(
                "DeepDelete - 准备汇总: 条目={0}, 删除条目数={1}, 媒体源数={2}, 快捷方式数={3}, 软链接数={4}, 挂载路径数={5}, 本地={6}, 远程={7}, 静态源缺失={8}, 分类失败={9}, 重复跳过={10}",
                item?.Name ?? "unknown",
                deleteItems.Count,
                totalMediaSources,
                shortcutSources,
                symlinkSources,
                mountPaths.Count,
                addedLocal,
                addedRemote,
                staticSourceMiss,
                classifyFail,
                duplicateSkip);

            return mountPaths;
        }

        private bool TryResolveStrmTargetPath(string strmPath, out string mountPath, out string reason) {
            mountPath = null;
            reason = null;

            if (string.IsNullOrWhiteSpace(strmPath)) {
                reason = "strmPathEmpty";
                return false;
            }

            if (!File.Exists(strmPath)) {
                reason = "strmFileNotFound";
                return false;
            }

            try {
                var line = File.ReadLines(strmPath)
                    .Select(l => l?.Trim())
                    .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#", StringComparison.Ordinal));

                if (string.IsNullOrWhiteSpace(line)) {
                    reason = "strmContentEmpty";
                    return false;
                }

                if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                    string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                    mountPath = uri.LocalPath;
                else
                    mountPath = line;

                if (string.IsNullOrWhiteSpace(mountPath)) {
                    reason = "parsedMountPathEmpty";
                    return false;
                }

                reason = "ok";
                return true;
            }
            catch (Exception ex) {
                reason = "strmReadError:" + ex.Message;
                return false;
            }
        }

        public void ExecuteDeepDelete(HashSet<string> mountPaths) {
            if (mountPaths == null || mountPaths.Count == 0) return;

            var deletePaths = new HashSet<FileSystemMetadata>(new FileSystemMetadataComparer());

            foreach (var mountPath in mountPaths)
            foreach (var path in GetDeletePaths(mountPath)) {
                deletePaths.Add(path);
                var folderPath = fileSystem.GetDirectoryName(path.FullName);

                if (!string.IsNullOrEmpty(folderPath))
                    deletePaths.Add(new FileSystemMetadata { FullName = folderPath, IsDirectory = true });
            }

            foreach (var path in deletePaths.Where(p => !p.IsDirectory))
                try {
                    fileSystem.DeleteFile(path.FullName, true);
                    logger.Info("DeepDelete - 删除文件成功: " + path.FullName);
                }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException) {
                    logger.Debug("DeepDelete - 文件已不存在或父目录缺失: " + path.FullName);
                    logger.Debug(e.Message);
                }
                catch (Exception e) {
                    logger.Error("DeepDelete - 删除文件失败: " + path.FullName);
                    logger.Error(e.Message);
                    logger.Debug(e.StackTrace);
                }

            var folderPaths = new HashSet<FileSystemMetadata>(deletePaths.Where(p => p.IsDirectory),
                new FileSystemMetadataComparer());

            while (folderPaths.Any()) {
                var path = folderPaths.First();

                try {
                    if (IsDirectoryEmpty(path.FullName)) {
                        logger.Info("DeepDelete - 尝试删除空目录: " + path.FullName);
                        fileSystem.DeleteDirectory(path.FullName, true, true);

                        var parentPath = fileSystem.GetDirectoryName(path.FullName);
                        if (!string.IsNullOrEmpty(parentPath))
                            folderPaths.Add(new FileSystemMetadata { FullName = parentPath, IsDirectory = true });
                    }
                }
                catch (DirectoryNotFoundException e) {
                    logger.Debug("DeepDelete - 目录已不存在: " + path.FullName);
                    logger.Debug(e.Message);
                }
                catch (Exception e) {
                    logger.Error("DeepDelete - 删除空目录失败: " + path.FullName);
                    logger.Error(e.Message);
                    logger.Debug(e.StackTrace);
                }

                folderPaths.Remove(path);
            }
        }

        private FileSystemMetadata[] GetDeletePaths(string path) {
            var folder = fileSystem.GetDirectoryName(path);

            if (string.IsNullOrEmpty(folder) || !fileSystem.DirectoryExists(folder)) return Array.Empty<FileSystemMetadata>();

            var basename = Path.GetFileNameWithoutExtension(path);
            var relatedFiles = GetRelatedPaths(basename, folder);
            var target = fileSystem.GetFileInfo(path);

            return new[] { target }.Concat(relatedFiles).Where(f => f?.Exists == true).ToArray();
        }

        private FileSystemMetadata[] GetRelatedPaths(string basename, string folder) {
            var extensions = new List<string> {
                ".nfo",
                ".xml",
                ".srt",
                ".vtt",
                ".sub",
                ".idx",
                ".txt",
                ".edl",
                ".bif",
                ".smi",
                ".ttml",
                ".ass",
                ".json"
            };

            extensions.AddRange(BaseItem.SupportedImageExtensions);

            return fileSystem.GetFiles(folder, extensions.ToArray(), false, false)
                .Where(i => !string.IsNullOrEmpty(i.FullName) && Path.GetFileNameWithoutExtension(i.FullName)
                    .StartsWith(basename, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public static bool IsFileShortcut(string path) {
            return path != null && string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryClassifyMountPath(string path, out string pathKey, out bool isLocal) {
            pathKey = null;
            isLocal = false;
            if (string.IsNullOrWhiteSpace(path)) return false;

            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri) {
                if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)) {
                    pathKey = uri.LocalPath;
                    isLocal = !string.IsNullOrWhiteSpace(pathKey);
                    return isLocal;
                }

                pathKey = path;
                isLocal = false;
                return true;
            }

            pathKey = path;
            isLocal = true;
            return true;
        }

        private static bool IsSymlink(string path) {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try {
                if (!File.Exists(path)) return false;

                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch {
                return false;
            }
        }

        private static string GetSymlinkTarget(string path) {
            try {
                var fileInfo = new FileInfo(path);
                var targetInfo = fileInfo.ResolveLinkTarget(false);
                return targetInfo?.FullName;
            }
            catch {
                return null;
            }
        }

        private static bool IsDirectoryEmpty(string path) {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

            return !Directory.EnumerateFileSystemEntries(path).Any();
        }

        private sealed class FileSystemMetadataComparer : IEqualityComparer<FileSystemMetadata> {
            public bool Equals(FileSystemMetadata x, FileSystemMetadata y) {
                if (x == null || y == null) return false;

                return x.IsDirectory == y.IsDirectory &&
                       string.Equals(x.FullName, y.FullName, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(FileSystemMetadata obj) {
                if (obj == null || string.IsNullOrEmpty(obj.FullName)) return 0;

                return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FullName) ^ obj.IsDirectory.GetHashCode();
            }
        }
    }
}
