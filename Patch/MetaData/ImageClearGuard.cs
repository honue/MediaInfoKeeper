using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace MediaInfoKeeper.Patch {
    /// <summary>
    ///     保护条目在图片刷新未产出替代图时不误删外部刮削的本地图片。
    /// </summary>
    public static class ImageClearGuard {
        private static Harmony harmony;
        private static MethodInfo clearImages;
        private static ILogger logger;
        private static bool isEnabled;

        public static bool IsReady => harmony != null && clearImages != null;

        public static void Initialize(ILogger pluginLogger, bool enableGuard) {
            if (harmony != null) return;

            logger = pluginLogger;
            isEnabled = enableGuard;

            try {
                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders?.GetType("Emby.Providers.Manager.ProviderManager");
                if (providerManager == null) {
                    PatchLog.InitFailed(logger, nameof(ImageClearGuard), "未找到 ProviderManager");
                    return;
                }

                clearImages = ResolveClearImages(providerManager.Assembly);
                if (clearImages == null) {
                    PatchLog.InitFailed(logger, nameof(ImageClearGuard), "未找到 ClearImages 重载");
                    return;
                }

                harmony = new Harmony("mediainfokeeper.itemimageclear");
                PatchLog.Patched(logger, nameof(ImageClearGuard), clearImages);
                harmony.Patch(clearImages, new HarmonyMethod(typeof(ImageClearGuard), nameof(ClearImagesPrefix)));
            }
            catch (Exception ex) {
                logger?.Error("ItemImageClearGuard 初始化失败");
                logger?.Error(ex.Message);
                logger?.Error(ex.ToString());
                harmony = null;
            }
        }

        public static void Configure(bool enableGuard) {
            isEnabled = enableGuard;
        }

        private static void ClearImagesPrefix(BaseItem item, ref ImageType[] imageTypesToClear,
            ref int numBackdropToKeep) {
            if (!isEnabled ||
                item == null ||
                item.InternalId == 0 ||
                imageTypesToClear == null)
                return;

            var protectedImages = GetProtectedImages(item);
            if (protectedImages.Length == 0) return;

            var externalImageCount = protectedImages.Count(image => IsOutsideItemFolder(
                image.Path,
                Path.GetDirectoryName(item.Path ?? item.FileName)));
            if (externalImageCount > 0)
                logger?.Debug("图片清理保护: item={0}, 外部缓存本地图={1}",
                    item.FileName ?? item.Path ?? item.InternalId.ToString(), externalImageCount);

            var clearTypes = imageTypesToClear;
            var protectedClearTypes = protectedImages
                .Where(image => image.Type != ImageType.Backdrop && clearTypes.Contains(image.Type))
                .Select(image => image.Type)
                .Distinct()
                .ToArray();

            if (protectedClearTypes.Length > 0)
                // SaveImage succeeds before ClearImages, so removing these types here only protects
                // existing local scraper images when no replacement was actually written.
                imageTypesToClear = imageTypesToClear
                    .Where(imageType => !protectedClearTypes.Contains(imageType))
                    .ToArray();

            if (numBackdropToKeep <= 0) {
                var protectedBackdropKeepCount = GetBackdropKeepCount(item, protectedImages);
                if (protectedBackdropKeepCount > numBackdropToKeep) numBackdropToKeep = protectedBackdropKeepCount;
            }
        }

        private static int GetBackdropKeepCount(BaseItem item, ProtectedImage[] protectedImages) {
            if (!protectedImages.Any(image => image.Type == ImageType.Backdrop)) return 0;

            var backdropIndex = 0;
            var keepCount = 0;
            foreach (var image in item.ImageInfos ?? Array.Empty<ItemImageInfo>()) {
                if (image.Type != ImageType.Backdrop) continue;

                if (protectedImages.Any(protectedImage =>
                        protectedImage.Type == ImageType.Backdrop &&
                        PathsEqual(protectedImage.Path, image.Path)))
                    keepCount = backdropIndex + 1;

                backdropIndex++;
            }

            return keepCount;
        }

        private static ProtectedImage[] GetProtectedImages(BaseItem item) {
            var itemPath = string.IsNullOrWhiteSpace(item.Path) ? item.FileName : item.Path;
            var folder = string.IsNullOrWhiteSpace(itemPath) ? null : Path.GetDirectoryName(itemPath);
            var basename = string.IsNullOrWhiteSpace(itemPath) ? null : Path.GetFileNameWithoutExtension(itemPath);

            // Emby accepts both item-prefixed names (movie-poster.jpg) and the
            // directory-level names (poster.jpg, fanart.jpg, clearlogo.png).
            // The latter are especially
            // common for STRM libraries and must be protected as well.  Keep
            // the candidate list explicit so unrelated local images are not
            // accidentally exempted from cleanup.
            var candidateImages = string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(basename)
                ? Array.Empty<ProtectedImage>()
                : new[] {
                    (Type: ImageType.Primary, Suffix: "-poster", DirectoryName: string.Empty),
                    (Type: ImageType.Primary, Suffix: string.Empty, DirectoryName: "poster"),
                    (Type: ImageType.Backdrop, Suffix: "-fanart", DirectoryName: string.Empty),
                    (Type: ImageType.Backdrop, Suffix: string.Empty, DirectoryName: "fanart"),
                    (Type: ImageType.Backdrop, Suffix: "-backdrop", DirectoryName: string.Empty),
                    (Type: ImageType.Backdrop, Suffix: string.Empty, DirectoryName: "backdrop"),
                    (Type: ImageType.Thumb, Suffix: "-thumb", DirectoryName: string.Empty),
                    (Type: ImageType.Thumb, Suffix: string.Empty, DirectoryName: "thumb"),
                    (Type: ImageType.Logo, Suffix: "-logo", DirectoryName: string.Empty),
                    (Type: ImageType.Logo, Suffix: "-clearlogo", DirectoryName: string.Empty),
                    (Type: ImageType.Logo, Suffix: string.Empty, DirectoryName: "clearlogo"),
                    (Type: ImageType.Banner, Suffix: "-banner", DirectoryName: string.Empty),
                    (Type: ImageType.Banner, Suffix: string.Empty, DirectoryName: "banner"),
                    (Type: ImageType.Disc, Suffix: "-disc", DirectoryName: string.Empty),
                    (Type: ImageType.Disc, Suffix: string.Empty, DirectoryName: "disc")
                }
                .Select(candidate => new ProtectedImage(
                    candidate.Type,
                    FindSiblingImagePath(folder,
                        string.IsNullOrEmpty(candidate.DirectoryName)
                            ? basename + candidate.Suffix
                            : candidate.DirectoryName)))
                .Where(image => !string.IsNullOrWhiteSpace(image.Path))
                .ToArray();

            var localImages = (item.ImageInfos ?? Array.Empty<ItemImageInfo>())
                .Where(image => image != null)
                .Select(image => new ProtectedImage(image.Type, image.Path))
                // When "save images to media folder" is disabled, Emby stores
                // the same local image records under its metadata/cache root.
                // Some versions mark those records as remote even though the
                // image is already present on disk, so the path check is the
                // reliable signal here.
                .Where(image => !string.IsNullOrWhiteSpace(image.Path) && File.Exists(image.Path))
                .Where(image => candidateImages.Any(candidateImage =>
                                     candidateImage.Type == image.Type &&
                                     PathsEqual(candidateImage.Path, image.Path)) ||
                                string.IsNullOrWhiteSpace(folder) ||
                                IsOutsideItemFolder(image.Path, folder))
                .GroupBy(image => image.Type + "\n" + NormalizePath(image.Path))
                .Select(group => group.First())
                .ToArray();

            return localImages;
        }

        private static string FindSiblingImagePath(string folder, string filenameWithoutExtension) {
            foreach (var extension in BaseItem.SupportedImageExtensions) {
                var path = Path.Combine(folder, filenameWithoutExtension + extension);
                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static bool PathsEqual(string left, string right) {
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOutsideItemFolder(string imagePath, string itemFolder) {
            if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(itemFolder)) return false;

            var normalizedImagePath = NormalizePath(imagePath);
            var normalizedFolder = NormalizePath(itemFolder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\');
            if (string.IsNullOrWhiteSpace(normalizedImagePath) || string.IsNullOrWhiteSpace(normalizedFolder))
                return false;

            var folderPrefix = normalizedFolder + Path.DirectorySeparatorChar;
            return !normalizedImagePath.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path) {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            try {
                return Path.GetFullPath(path);
            }
            catch {
                return path;
            }
        }

        private static MethodInfo ResolveClearImages(Assembly embyProvidersAssembly) {
            try {
                var itemImageProvider = embyProvidersAssembly?.GetType("Emby.Providers.Manager.ItemImageProvider");
                if (itemImageProvider == null) return null;

                var embyProvidersVersion = embyProvidersAssembly.GetName().Version;
                return PatchMethodResolver.Resolve(
                    itemImageProvider,
                    embyProvidersVersion,
                    new MethodSignatureProfile {
                        Name = "itemimageprovider-clearimages-exact",
                        MethodName = "ClearImages",
                        BindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        ParameterTypes = new[] {
                            typeof(BaseItem),
                            typeof(ImageType[]),
                            typeof(int)
                        },
                        ReturnType = typeof(void),
                        IsStatic = false
                    },
                    logger,
                    "ItemImageClearGuard.ClearImages");
            }
            catch {
                return null;
            }
        }

        private readonly struct ProtectedImage {
            public ProtectedImage(ImageType type, string path) {
                Type = type;
                Path = path;
            }

            public ImageType Type { get; }

            public string Path { get; }
        }
    }
}
