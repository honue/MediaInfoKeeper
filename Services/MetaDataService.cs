using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaInfoKeeper.Patch;

namespace MediaInfoKeeper.Services {
    public class MetaDataService {
        private readonly IProviderManager providerManager;

        public MetaDataService(IProviderManager providerManager) {
            this.providerManager = providerManager;
        }

        internal async Task RefreshMetaDataAsync(
            BaseItem item,
            MetadataRefreshOptions options,
            CancellationToken cancellationToken,
            bool allowFfProcess = false) {
            if (item == null || options == null) return;

            using var externalImageProtection = ImageClearGuard.BeginExternalImageProtection(options);

            if (allowFfProcess) {
                using (FfProcessGuard.Allow()) {
                    await providerManager
                        .RefreshFullItem(item, options, cancellationToken)
                        .ConfigureAwait(false);
                }

                return;
            }

            await providerManager
                .RefreshFullItem(item, options, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
