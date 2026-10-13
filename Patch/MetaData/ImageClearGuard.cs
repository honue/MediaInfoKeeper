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
            var itemPath = item.Path ?? item.FileName;
            if (string.IsNullOrWhiteSpace(itemPath)) return Array.Empty<ProtectedImage>();

            var folder = Path.GetDirectoryName(itemPath);
            var basename = Path.GetFileNameWithoutExtension(itemPath);
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(basename)) return Array.Empty<ProtectedImage>();

            var prefixedImages = new[] {
                    (Type: ImageType.Primary, Suffix: "-poster"),
                    (Type: ImageType.Backdrop, Suffix: "-fanart"),
                    (Type: ImageType.Thumb, Suffix: "-thumb")
                }
                .Select(candidate => new ProtectedImage(
                    candidate.Type,
                    FindSiblingImagePath(folder, basename + candidate.Suffix)))
                .Where(image => !string.IsNullOrWhiteSpace(image.Path))
                .ToArray();

            if (prefixedImages.Length == 0) return Array.Empty<ProtectedImage>();

            return (item.ImageInfos ?? Array.Empty<ItemImageInfo>())
                .Where(image => image != null && image.IsLocalFile)
                .Select(image => new ProtectedImage(image.Type, image.Path))
                .Where(image => prefixedImages.Any(prefixedImage =>
                    prefixedImage.Type == image.Type &&
                    PathsEqual(prefixedImage.Path, image.Path)))
                .GroupBy(image => image.Type + "\n" + NormalizePath(image.Path))
                .Select(group => group.First())
                .ToArray();
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
