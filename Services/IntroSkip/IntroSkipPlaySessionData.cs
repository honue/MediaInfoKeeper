using MediaBrowser.Controller.Entities;
using System;

namespace MediaInfoKeeper.Services.IntroSkip
{
    public class IntroSkipPlaySessionData
    {
        public IntroSkipPlaySessionData(BaseItem item)
        {
            IntroStart = Plugin.IntroSkipChapterApi.GetIntroStart(item);
            IntroEnd = Plugin.IntroSkipChapterApi.GetIntroEnd(item);
            CreditsStart = Plugin.IntroSkipChapterApi.GetCreditsStart(item);

            var options = Plugin.Instance.Options.IntroSkip;
            MaxIntroDurationTicks = options.MaxIntroDurationSeconds * TimeSpan.TicksPerSecond;
            MaxCreditsDurationTicks = options.MaxCreditsDurationSeconds * TimeSpan.TicksPerSecond;
            MinOpeningPlotDurationTicks = options.MinOpeningPlotDurationSeconds * TimeSpan.TicksPerSecond;
        }

        public long? IntroStart { get; set; }

        public long? IntroEnd { get; set; }

        public long? CreditsStart { get; set; }

        public long PlaybackStartTicks { get; set; } = 0;

        public long PreviousPositionTicks { get; set; } = 0;

        public DateTime PreviousEventTime { get; set; } = DateTime.MinValue;

        public long? FirstJumpPositionTicks { get; set; } = null;

        public long? LastJumpPositionTicks { get; set; } = null;

        public long MaxIntroDurationTicks { get; set; }

        public long MaxCreditsDurationTicks { get; set; }

        public long MinOpeningPlotDurationTicks { get; set; }

        public DateTime? LastPauseEventTime { get; set; } = null;

        public DateTime? LastPlaybackRateChangeEventTime { get; set; } = null;
    }
}
