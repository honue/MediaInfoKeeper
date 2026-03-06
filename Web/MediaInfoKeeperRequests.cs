using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace MediaInfoKeeper.Web
{
    [Route("/{Web}/components/mediainfokeeper/mediainfokeeper.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class MediaInfoKeeperJsRequest
    {
        public string Web { get; set; }

        public string ResourceName { get; set; }
    }

    [Route("/{Web}/modules/shortcuts.js", "GET", IsHidden = true)]
    [Unauthenticated]
    public class ShortcutMenuRequest
    {
        public string Web { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ExtractMediaInfo", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ExtractMediaInfoRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/DeleteMediaInfoPersist", "POST")]
    [Authenticated(Roles = "Admin")]
    public class DeleteMediaInfoPersistRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ScanIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ScanIntroRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/SetIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SetIntroRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
        public long IntroStartTicks { get; set; }
        public long IntroEndTicks { get; set; }
    }

    [Route("/MediaInfoKeeper/Items/ClearIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearIntroRequest : IReturn<MediaInfoMenuResponse>
    {
        public string[] Ids { get; set; }
    }

    public class MediaInfoMenuResponse
    {
        public int Total { get; set; }

        public int Processed { get; set; }

        public int Succeeded { get; set; }

        public int Failed { get; set; }

        public int Skipped { get; set; }

        public string Message { get; set; }
    }

}