using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     收敛因同一 TMDB/IMDb 人员 ID 被建立为多个本地 Person 而产生的演员页分裂。
    /// </summary>
    internal sealed class DuplicatePersonCleanup {
        private static readonly MetadataProviders[] IdentityProviders = {
            MetadataProviders.Tmdb,
            MetadataProviders.Imdb
        };

        private readonly ILibraryManager libraryManager;
        private readonly ILogger logger;

        public DuplicatePersonCleanup(ILibraryManager libraryManager, ILogger logger) {
            this.libraryManager = libraryManager;
            this.logger = logger;
        }

        public CleanupResult Execute(CancellationToken cancellationToken = default) {
            if (libraryManager == null) return new CleanupResult();

            cancellationToken.ThrowIfCancellationRequested();
            var allPeople = ReadAllPeople(cancellationToken);
            var duplicateGroups = FindDuplicateGroups(allPeople);
            if (duplicateGroups.Count == 0) {
                logger?.Info("演员去重 - 未发现共享 TMDB/IMDb 人员 ID 的重复演员");
                return new CleanupResult();
            }

            var duplicatePersonIds = duplicateGroups
                .SelectMany(group => group.PersonIds)
                .ToHashSet();
            var affectedItems = allPeople
                .Where(person => duplicatePersonIds.Contains(person.Id) && person.ItemId > 0)
                .Select(person => person.ItemId)
                .ToHashSet();
            var result = new CleanupResult {
                DuplicateIdentityGroups = duplicateGroups.Count,
                DuplicatePersonCandidates = duplicatePersonIds.Count
            };

            logger?.Info(
                "演员去重 - 识别到身份组={0}，候选人物={1}，待重建关联条目={2}",
                result.DuplicateIdentityGroups,
                result.DuplicatePersonCandidates,
                affectedItems.Count);

            // Emby 在 UpdatePeople 时按 PersonInfo 的 ProviderIds 查找/复用 Person。
            // 重新写入所有受影响条目可令相同外部人员 ID 收敛到一个 Person，随后再删除无关联的旧 Person。
            foreach (var itemPeople in allPeople.GroupBy(person => person.ItemId)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!affectedItems.Contains(itemPeople.Key)) continue;

                var item = libraryManager.GetItemById(itemPeople.Key);
                if (item == null) {
                    result.FailedItems++;
                    logger?.Warn("演员去重 - 找不到关联条目 itemId={0}", itemPeople.Key);
                    continue;
                }

                try {
                    libraryManager.UpdatePeople(item, itemPeople.ToList(), false);
                    result.RewrittenItems++;
                }
                catch (Exception ex) {
                    result.FailedItems++;
                    logger?.Error("演员去重 - 重建人物关联失败 itemId={0}: {1}", itemPeople.Key, ex.Message);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var rewrittenPeople = ReadAllPeople(cancellationToken);
            foreach (var itemPeople in rewrittenPeople.GroupBy(person => person.ItemId)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!affectedItems.Contains(itemPeople.Key)) continue;

                var people = itemPeople.ToList();
                var deduplicatedPeople = RemoveExactDuplicateCredits(people);
                if (deduplicatedPeople.Count == people.Count) continue;

                var item = libraryManager.GetItemById(itemPeople.Key);
                if (item == null) {
                    result.FailedItems++;
                    logger?.Warn("演员去重 - 找不到去重后的关联条目 itemId={0}", itemPeople.Key);
                    continue;
                }

                try {
                    libraryManager.UpdatePeople(item, deduplicatedPeople, false);
                    result.RemovedDuplicateCredits += people.Count - deduplicatedPeople.Count;
                }
                catch (Exception ex) {
                    result.FailedItems++;
                    logger?.Error("演员去重 - 清理重复演职员关联失败 itemId={0}: {1}", itemPeople.Key, ex.Message);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var referencedDuplicatePersonIds = ReadAllPeople(cancellationToken)
                .Where(person => duplicatePersonIds.Contains(person.Id))
                .Select(person => person.Id)
                .ToHashSet();
            var orphanedDuplicatePersonIds = duplicatePersonIds
                .Where(id => !referencedDuplicatePersonIds.Contains(id))
                .ToArray();

            if (orphanedDuplicatePersonIds.Length > 0) {
                libraryManager.DeleteItems(orphanedDuplicatePersonIds);
                result.DeletedOrphanedPersons = orphanedDuplicatePersonIds.Length;
            }

            logger?.Info(
                "演员去重完成: 身份组={0}，重建条目={1}，重复关联={2}，删除孤立人物={3}，失败条目={4}",
                result.DuplicateIdentityGroups,
                result.RewrittenItems,
                result.RemovedDuplicateCredits,
                result.DeletedOrphanedPersons,
                result.FailedItems);
            return result;
        }

        private List<PersonInfo> ReadAllPeople(CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            return libraryManager.GetItemPeople(new InternalPeopleQuery {
                EnableIds = true,
                EnableProviderIds = true
            }) ?? new List<PersonInfo>();
        }

        private static List<DuplicateGroup> FindDuplicateGroups(IEnumerable<PersonInfo> people) {
            return people
                .Where(person => person != null && person.Id > 0)
                .SelectMany(person => GetIdentityKeys(person)
                    .Select(identity => new { Identity = identity, PersonId = person.Id }))
                .GroupBy(value => value.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(group => new DuplicateGroup {
                    PersonIds = group.Select(value => value.PersonId).Distinct().ToArray()
                })
                .Where(group => group.PersonIds.Length > 1)
                .ToList();
        }

        private static IEnumerable<string> GetIdentityKeys(PersonInfo person) {
            foreach (var provider in IdentityProviders) {
                var providerId = person.GetProviderId(provider)?.Trim();
                if (string.IsNullOrWhiteSpace(providerId)) continue;

                yield return provider + "\u001f" + providerId;
            }
        }

        private static List<PersonInfo> RemoveExactDuplicateCredits(IEnumerable<PersonInfo> people) {
            return people
                .GroupBy(person => new CreditIdentity(person.Id, person.Type, person.Role ?? string.Empty))
                .Select(group => group.First())
                .ToList();
        }

        private sealed class DuplicateGroup {
            public long[] PersonIds { get; set; } = Array.Empty<long>();
        }

        private readonly struct CreditIdentity : IEquatable<CreditIdentity> {
            private readonly long personId;
            private readonly PersonType personType;
            private readonly string role;

            public CreditIdentity(long personId, PersonType personType, string role) {
                this.personId = personId;
                this.personType = personType;
                this.role = role;
            }

            public bool Equals(CreditIdentity other) {
                return personId == other.personId &&
                       personType == other.personType &&
                       string.Equals(role, other.role, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is CreditIdentity other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(personId, personType, role);
        }

        internal sealed class CleanupResult {
            public int DuplicateIdentityGroups { get; set; }

            public int DuplicatePersonCandidates { get; set; }

            public int RewrittenItems { get; set; }

            public int RemovedDuplicateCredits { get; set; }

            public int DeletedOrphanedPersons { get; set; }

            public int FailedItems { get; set; }
        }
    }
}
