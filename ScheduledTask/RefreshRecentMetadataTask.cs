using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.Services;
using static MediaInfoKeeper.Options.MainPageOptions;

namespace MediaInfoKeeper.ScheduledTask
{
    public class RefreshRecentMetadataTask : IScheduledTask
    {
        private sealed class RoleRefreshTarget
        {
            public long ItemId { get; set; }

            public string Name { get; set; }

            public int? ProductionYear { get; set; }
        }

        private readonly ILogger logger;
        private readonly ILibraryManager libraryManager;

        public RefreshRecentMetadataTask(ILogManager logManager, ILibraryManager libraryManager)
        {
            this.logger = logManager.GetLogger(Plugin.PluginName);
            this.libraryManager = libraryManager;
        }
        public string Key => "MediaInfoKeeperRefreshRecentMetadataTask";

        public string Name => "02.刷新媒体元数据";

        public string Description => "按本任务配置的媒体库范围与最近入库时间窗口筛选条目，刷新元数据（可选覆盖或补全），之后会从 JSON 恢复媒体信息。";

        public string Category => Plugin.TaskCategoryName;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            this.logger.Info("最近条目刷新元数据计划任务开始");

            var items = FetchRecentItems();
            var total = items.Count;
            if (total == 0)
            {
                progress.Report(100.0);
                this.logger.Info("计划任务完成，条目数 0");
                return;
            }

            var replaceMetadata = ShouldReplaceMetadata();
            var replaceImages = ShouldReplaceImages();
            var replaceThumbnails = ShouldReplaceThumbnails();
            var metadataRefreshTargets = CollectMetadataRefreshItemIds(items);
            var totalWork = total + metadataRefreshTargets.Count;
            this.logger.Info($"计划任务条目数{total}，元数据覆盖{replaceMetadata}，图片覆盖{replaceImages}，视频缩略图覆盖{replaceThumbnails}");

            var completed = 0;
            var tasks = items
                .Select(async item =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ProcessItemAsync(item, replaceMetadata, replaceImages, replaceThumbnails, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        this.logger.Info($"计划任务已取消 {item.Path}");
                    }
                    catch (Exception e)
                    {
                        this.logger.Error($"计划任务失败: {item.Path}");
                        this.logger.Error(e.Message);
                        this.logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        ReportProgress(totalWork, progress, Interlocked.Increment(ref completed));
                    }
                })
                .ToList();
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (metadataRefreshTargets.Count > 0)
            {
                this.logger.Info($"计划任务刷新豆瓣角色中文化 {metadataRefreshTargets.Count} 个 Movie/Series");
            }
            foreach (var target in metadataRefreshTargets)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RefreshMetadataForDoubanRoleAsync(target, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    this.logger.Info($"计划任务已取消 itemid={target.ItemId}");
                    throw;
                }
                catch (Exception e)
                {
                    this.logger.Error($"计划任务刷新豆瓣演员角色元数据失败: itemid={target.ItemId}");
                    this.logger.Error(e.Message);
                    this.logger.Debug(e.StackTrace);
                }
                finally
                {
                    ReportProgress(totalWork, progress, Interlocked.Increment(ref completed));
                }
            }

            this.logger.Info("最近条目刷新元数据计划任务完成");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            };

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(16).Ticks
            };
        }

        private List<BaseItem> FetchRecentItems()
        {
            var taskOptions = Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata;
            var taskScope = taskOptions.RefreshRecentMetadataLibraries;
            var days = taskOptions.RefreshRecentMetadataDays;
            var cutoff = days > 0
                ? ConfiguredDateTime.Now.AddDays(-days)
                : (DateTime?)null;

            return Plugin.LibraryService.FetchRecentScheduledTaskLibraryItems(
                cutoff,
                taskScope,
                true,
                includeAudio: true);
        }

        private MetadataRefreshOptions BuildRefreshOptions(bool replaceMetadata, bool replaceImages, bool replaceThumbnails)
        {
            return new MetadataRefreshOptions(new DirectoryService(this.logger, Plugin.FileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = replaceMetadata,
                ReplaceAllImages = replaceImages,
                ReplaceThumbnailImages = replaceThumbnails,
                EnableThumbnailImageExtraction = Plugin.Instance.Options.MetaData.EnableImageCapture,
                IsAutomated = true
            };
        }

        private async Task ProcessItemAsync(
            BaseItem item,
            bool replaceMetadata,
            bool replaceImages,
            bool replaceThumbnails,
            CancellationToken cancellationToken)
        {
            var created = item.DateCreated == default
                ? "unknown"
                : ConfiguredDateTime.ToConfiguredOffset(item.DateCreated).ToString("yyyy-MM-dd HH:mm:ss zzz");
            this.logger.Info($"刷新元数据 {item.FileName ?? item.Path} 入库日期 = {created}");

            var options = BuildRefreshOptions(replaceMetadata, replaceImages, replaceThumbnails);
            var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);

            await RefreshTaskRunner.RunAsync(
                    () => Plugin.ProviderManager
                        .RefreshSingleItem(item, options, collectionFolders, libraryOptions, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            // 刷新完元数据要重新从json恢复媒体信息，
            // 非strm会重新 ffprobe/ffmpeg，但是没有allow所以会拦截，
            // strm由 MediaInfoClearGuard 防止被写空，不必重复恢复
            // Plugin.MediaSourceInfoStore.ApplyToItem(item);
            // if (item is Video)
            // {
            //     Plugin.ChaptersStore.ApplyToItem(item);
            // }
            // else if (item is Audio)
            // {
            //     Plugin.EmbeddedInfoStore.ApplyToItem(item);
            // }
        }

        private async Task RefreshMetadataForDoubanRoleAsync(RoleRefreshTarget target, CancellationToken cancellationToken)
        {
            var item = this.libraryManager.GetItemById(target.ItemId) as BaseItem;
            if (item == null)
            {
                this.logger.Info($"跳过演员角色元数据刷新: 未找到 {FormatItemLabel(target.Name, target.ProductionYear)} itemid={target.ItemId}");
                return;
            }

            this.logger.Info($"刷新演员角色元数据 {FormatItemLabel(item.Name, item.ProductionYear)}");

            var beforeRoles = BuildPeopleRoleMap(this.libraryManager.GetItemPeople(item));

            var options = BuildRefreshOptions(replaceMetadata: true, replaceImages: false, replaceThumbnails: false);
            options.Recursive = false;
            var collectionFolders = this.libraryManager.GetCollectionFolders(item).Cast<BaseItem>().ToArray();
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);

            await RefreshTaskRunner.RunAsync(
                    () => Plugin.ProviderManager
                        .RefreshSingleItem(item, options, collectionFolders, libraryOptions, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            var afterRoles = BuildPeopleRoleMap(this.libraryManager.GetItemPeople(item));
            var updatedRoles = afterRoles
                .Where(entry =>
                    entry.Value.HasChineseRole &&
                    beforeRoles.TryGetValue(entry.Key, out var before) &&
                    !string.Equals(before.Role, entry.Value.Role, StringComparison.Ordinal))
                .Select(entry =>
                {
                    var before = beforeRoles[entry.Key];
                    return $"{entry.Value.Name}: {FormatRoleValue(before.Role)} -> {FormatRoleValue(entry.Value.Role)}";
                })
                .ToList();

            if (updatedRoles.Count > 0)
            {
                this.logger.Info($"豆瓣演员角色已更新 {FormatItemLabel(item.Name, item.ProductionYear)}: {string.Join(", ", updatedRoles)}");
            }

            if (item is Series series)
            {
                PropagateSeriesPeopleRoles(series);
            }
        }

        private List<RoleRefreshTarget> CollectMetadataRefreshItemIds(IEnumerable<BaseItem> items)
        {
            var result = new List<RoleRefreshTarget>();
            var seen = new HashSet<long>();

            foreach (var item in items)
            {
                BaseItem refreshItem = item switch
                {
                    Movie movie when movie.InternalId > 0 => movie,
                    Series series when series.InternalId > 0 => series,
                    Season season when season.Series?.InternalId > 0 => season.Series,
                    Episode episode when episode.Series?.InternalId > 0 => episode.Series,
                    _ => null
                };

                if (!IsDoubanRoleEnabled(refreshItem))
                {
                    continue;
                }

                if (seen.Add(refreshItem.InternalId))
                {
                    result.Add(new RoleRefreshTarget
                    {
                        ItemId = refreshItem.InternalId,
                        Name = refreshItem.Name,
                        ProductionYear = refreshItem.ProductionYear
                    });
                }
            }

            return result;
        }

        private void PropagateSeriesPeopleRoles(Series series)
        {
            if (!IsDoubanRoleEnabled(series))
            {
                return;
            }

            var seriesPeople = this.libraryManager.GetItemPeople(series);
            if (seriesPeople == null || seriesPeople.Count == 0)
            {
                return;
            }

            var roleMap = seriesPeople
                .Where(p => p != null &&
                            (p.Type == PersonType.Actor || p.Type == PersonType.GuestStar) &&
                            LanguageUtility.IsChinese(p.Role))
                .GroupBy(p => NormalizePersonName(p.Name), StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                .ToDictionary(g => g.Key, g => g.First().Role, StringComparer.OrdinalIgnoreCase);

            if (roleMap.Count == 0)
            {
                return;
            }

            var children = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                SeriesIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { nameof(Season), nameof(Episode) },
                Recursive = true
            });

            foreach (var child in children)
            {
                var people = this.libraryManager.GetItemPeople(child);
                if (people == null || people.Count == 0)
                {
                    continue;
                }

                if (!TryApplySeriesRoleMap(people, roleMap))
                {
                    continue;
                }

                Plugin.Instance.ItemRepository.UpdatePeople(child.InternalId, people);
            }

        }

        private bool IsDoubanRoleEnabled(BaseItem item)
        {
            if (item == null)
            {
                return false;
            }

            var libraryOptions = this.libraryManager.GetLibraryOptions(item);
            return libraryOptions != null &&
                   item.IsMetadataFetcherEnabled(libraryOptions, Provider.DoubanRoleProvider.ProviderName);
        }

        private static bool TryApplySeriesRoleMap(
            IEnumerable<PersonInfo> people,
            IReadOnlyDictionary<string, string> roleMap)
        {
            var changed = false;
            foreach (var person in people)
            {
                if (!ShouldUpdateChildPersonRole(person, roleMap, out var chineseRole))
                {
                    continue;
                }

                person.Role = chineseRole;
                changed = true;
            }

            return changed;
        }

        private static bool ShouldUpdateChildPersonRole(
            PersonInfo person,
            IReadOnlyDictionary<string, string> roleMap,
            out string chineseRole)
        {
            chineseRole = null;
            if (person == null || (person.Type != PersonType.Actor && person.Type != PersonType.GuestStar))
            {
                return false;
            }

            var normalizedName = NormalizePersonName(person.Name);
            if (string.IsNullOrWhiteSpace(normalizedName) ||
                !roleMap.TryGetValue(normalizedName, out chineseRole) ||
                !LanguageUtility.IsChinese(chineseRole) ||
                string.Equals(person.Role, chineseRole, StringComparison.Ordinal))
            {
                chineseRole = null;
                return false;
            }

            return true;
        }

        private static string NormalizePersonName(string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? null
                : name.Trim().Replace("·", string.Empty).Replace(" ", string.Empty);
        }

        private static Dictionary<string, (string Name, string Role, bool HasChineseRole)> BuildPeopleRoleMap(IEnumerable<PersonInfo> people)
        {
            var result = new Dictionary<string, (string Name, string Role, bool HasChineseRole)>(StringComparer.OrdinalIgnoreCase);
            foreach (var person in people ?? Enumerable.Empty<PersonInfo>())
            {
                if (person == null || (person.Type != PersonType.Actor && person.Type != PersonType.GuestStar))
                {
                    continue;
                }

                var normalizedName = NormalizePersonName(person.Name);
                if (string.IsNullOrWhiteSpace(normalizedName))
                {
                    continue;
                }

                result[normalizedName] = (person.Name, person.Role, LanguageUtility.IsChinese(person.Role));
            }

            return result;
        }

        private static string FormatItemLabel(string name, int? productionYear)
        {
            return productionYear.HasValue
                ? $"{name} ({productionYear.Value})"
                : name ?? string.Empty;
        }

        private static string FormatRoleValue(string role)
        {
            return string.IsNullOrWhiteSpace(role) ? "空" : role;
        }

        private static void ReportProgress(int totalWork, IProgress<double> progress, int completed)
        {
            progress?.Report(completed / (double)totalWork * 100);
        }

        private bool ShouldReplaceMetadata()
        {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.RefreshMetadataMode == RefreshModeOption.Replace;
        }

        private bool ShouldReplaceImages()
        {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingImages;
        }

        private bool ShouldReplaceThumbnails()
        {
            return Plugin.Instance.Options.MainPage.ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingVideoPreviewThumbnails;
        }
    }
}
