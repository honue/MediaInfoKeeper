using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;

namespace MediaInfoKeeper.Provider {
    /// <summary>媒体库级入库片头扫描开关。</summary>
    public sealed class ItemAddedIntroScanProvider :
        ILocalMetadataProvider<Series>,
        ILocalMetadataProvider<Season>,
        ILocalMetadataProvider<Episode>,
        IHasOrder {
        public const string ProviderName = "MediaInfoKeeper 入库扫描片头";
        public const int DefaultOrder = int.MaxValue - 1;

        public string Name => ProviderName;

        public int Order => DefaultOrder;

        Task<MetadataResult<Series>> ILocalMetadataProvider<Series>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<Series>();
        }

        Task<MetadataResult<Season>> ILocalMetadataProvider<Season>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<Season>();
        }

        Task<MetadataResult<Episode>> ILocalMetadataProvider<Episode>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<Episode>();
        }

        private static Task<MetadataResult<T>> Empty<T>() where T : BaseItem {
            return Task.FromResult(new MetadataResult<T>());
        }
    }
}
