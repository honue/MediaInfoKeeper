using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace MediaInfoKeeper.Web {
    [Route("/{Web}/components/mediainfokeeper/mediainfokeeper.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class MediaInfoKeeperJsRequest {
        public string Web { get; set; }

        public string ResourceName { get; set; }
    }

    [Route("/{Web}/components/mediainfokeeper/ede.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class EdeJsRequest {
        public string Web { get; set; }
    }

    [Route("/{Web}/modules/shortcuts.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class ShortcutMenuRequest {
        public string Web { get; set; }
    }

    [Route("/{Web}/modules/refreshdialog/refreshdialog.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class RefreshDialogRequest {
        public string Web { get; set; }
    }

    [Route("/api/danmu/{ItemId}/raw", "GET")]
    [Unauthenticated]
    public class DanmuRawRequest : IReturnVoid {
        public string ItemId { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ExtractMediaInfo", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ExtractMediaInfoRequest : IReturn<MediaInfoMenuResponse> {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/DeleteMediaInfoPersist", "POST")]
    [Authenticated(Roles = "Admin")]
    public class DeleteMediaInfoPersistRequest : IReturn<MediaInfoMenuResponse> {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ScanIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ScanIntroRequest : IReturn<MediaInfoMenuResponse> {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ScanExternalFiles", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ScanExternalFilesRequest : IReturn<MediaInfoMenuResponse> {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/SetIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SetIntroRequest : IReturn<MediaInfoMenuResponse> {
        public string[] Ids { get; set; }
        public long IntroStartTicks { get; set; }
        public long IntroEndTicks { get; set; }
        public long? CreditsStartTicks { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ClearIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearIntroRequest : IReturn<MediaInfoMenuResponse> {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/DebugMediaInfo", "GET")]
    [Authenticated(Roles = "Admin")]
    public class DebugMediaInfoRequest : IReturn<DebugMediaInfoResponse> {
        public long InternalId { get; set; }
    }

    public class MediaInfoMenuResponse {
        public int Total { get; set; }

        public int Processed { get; set; }

        public int Succeeded { get; set; }

        public int Failed { get; set; }

        public int Skipped { get; set; }

        public string Message { get; set; }
    }

    public class DebugMediaInfoResponse {
        public bool Found { get; set; }

        public string Message { get; set; }

        public DebugItemInfo Item { get; set; }

        public DebugFileInfo MediaInfoJson { get; set; }

        public DebugPrimaryImageInfo PrimaryImage { get; set; }

        public DebugChapterImagesInfo ChapterImages { get; set; }

        public DebugThumbnailSetsInfo ThumbnailSets { get; set; }
    }

    public class DebugItemInfo {
        public long InternalId { get; set; }

        public string Type { get; set; }

        public string Name { get; set; }

        public string Path { get; set; }

        public string FileName { get; set; }

        public string ContainingFolderPath { get; set; }

        public string ItemId { get; set; }

        public long ParentId { get; set; }

        public long ImageDisplayParentId { get; set; }

        public bool IsShortcut { get; set; }

        public bool? IsRemote { get; set; }

        public string ExtraType { get; set; }

        public bool HasMediaInfo { get; set; }

        public bool HasCover { get; set; }

        public bool HasPrimaryImage { get; set; }

        public bool IsRefreshedRecently { get; set; }

        public int MediaStreamCount { get; set; }

        public int AudioStreamCount { get; set; }

        public int VideoStreamCount { get; set; }

        public int SubtitleStreamCount { get; set; }

        public long? RunTimeTicks { get; set; }

        public long Size { get; set; }

        public string Container { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public string DateCreated { get; set; }

        public string DateModified { get; set; }

        public string DateLastRefreshed { get; set; }

        public string PremiereDate { get; set; }

        public int? ProductionYear { get; set; }

        public string OfficialRating { get; set; }

        public bool? SupportsThumbnails { get; set; }
    }

    public class DebugPrimaryImageInfo {
        public bool HasPrimaryImage { get; set; }

        public string PrimaryImagePath { get; set; }

        public bool PrimaryImagePathExists { get; set; }

        public long ImageDisplayParentId { get; set; }

        public bool HasDisplayParentPrimaryImage { get; set; }

        public string DisplayParentPrimaryImagePath { get; set; }

        public bool DisplayParentPrimaryImagePathExists { get; set; }
    }

    public class DebugChapterImagesInfo {
        public int ChapterCount { get; set; }

        public int ChaptersWithImagePath { get; set; }

        public int ExistingImageFiles { get; set; }

        public DebugChapterImageEntry[] Entries { get; set; }
    }

    public class DebugChapterImageEntry {
        public string Name { get; set; }

        public string MarkerType { get; set; }

        public long StartPositionTicks { get; set; }

        public string ImagePath { get; set; }

        public bool ImagePathExists { get; set; }

        public string ImageTag { get; set; }

        public string ImageDateModified { get; set; }
    }

    public class DebugThumbnailSetsInfo {
        public bool SupportsThumbnails { get; set; }

        public int Count { get; set; }

        public DebugThumbnailSetEntry[] Entries { get; set; }
    }

    public class DebugThumbnailSetEntry {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public bool IsDirectory { get; set; }

        public int Width { get; set; }

        public int IntervalSeconds { get; set; }
    }

    public class DebugFileInfo {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public object Content { get; set; }
    }

    public class DebugBinaryFileInfo {
        public string Path { get; set; }

        public bool Exists { get; set; }

        public long Length { get; set; }
    }
}
