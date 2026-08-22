using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;

namespace MediaInfoKeeper.Provider {
    /// <summary>
    ///     媒体库级入库刮削开关。Provider 本身不导入元数据，启用状态由 OnItemAdded 使用。
    /// </summary>
    public sealed class ItemAddedRefreshProvider :
        ILocalMetadataProvider<Movie>,
        ILocalMetadataProvider<Series>,
        ILocalMetadataProvider<Season>,
        ILocalMetadataProvider<Episode>,
        ILocalMetadataProvider<Video>,
        ILocalMetadataProvider<MusicVideo>,
        ILocalMetadataProvider<Audio>,
        ILocalMetadataProvider<BoxSet>,
        IHasOrder {
        public const string ProviderName = "MediaInfoKeeper 入库刮削元数据";
        public const int DefaultOrder = int.MaxValue - 2;

        public string Name => ProviderName;

        public int Order => DefaultOrder;

        Task<MetadataResult<Movie>> ILocalMetadataProvider<Movie>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<Movie>();
        }

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

        Task<MetadataResult<Video>> ILocalMetadataProvider<Video>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<Video>();
        }

        Task<MetadataResult<MusicVideo>> ILocalMetadataProvider<MusicVideo>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<MusicVideo>();
        }

        Task<MetadataResult<Audio>> ILocalMetadataProvider<Audio>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<Audio>();
        }

        Task<MetadataResult<BoxSet>> ILocalMetadataProvider<BoxSet>.GetMetadata(
            ItemInfo info,
            LibraryOptions libraryOptions,
            IDirectoryService directoryService,
            CancellationToken cancellationToken) {
            return Empty<BoxSet>();
        }

        private static Task<MetadataResult<T>> Empty<T>() where T : BaseItem {
            return Task.FromResult(new MetadataResult<T>());
        }
    }
}
