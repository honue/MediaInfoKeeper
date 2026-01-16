using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MediaInfoKeeper.Services.IntroSkip
{
    public class IntroSkipPlaySessionMonitor
    {
        private readonly ILibraryManager libraryManager;
        private readonly IUserManager userManager;
        private readonly ISessionManager sessionManager;
        private readonly ILogger logger;

        private readonly TimeSpan updateInterval = TimeSpan.FromSeconds(10);
        private readonly ConcurrentDictionary<string, IntroSkipPlaySessionData> playSessionData =
            new ConcurrentDictionary<string, IntroSkipPlaySessionData>();
        private readonly ConcurrentDictionary<Episode, Task> ongoingIntroUpdates = new ConcurrentDictionary<Episode, Task>();
        private readonly ConcurrentDictionary<Episode, Task> ongoingCreditsUpdates = new ConcurrentDictionary<Episode, Task>();
        private readonly ConcurrentDictionary<Episode, DateTime> lastIntroUpdateTimes = new ConcurrentDictionary<Episode, DateTime>();
        private readonly ConcurrentDictionary<Episode, DateTime> lastCreditsUpdateTimes = new ConcurrentDictionary<Episode, DateTime>();
        private readonly object introLock = new object();
        private readonly object creditsLock = new object();

        public static List<string> LibraryPathsInScope { get; private set; } = new List<string>();
        public static User[] UsersInScope { get; private set; } = Array.Empty<User>();

        public IntroSkipPlaySessionMonitor(ILibraryManager libraryManager, IUserManager userManager,
            ISessionManager sessionManager, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.userManager = userManager;
            this.sessionManager = sessionManager;
            this.logger = logger;

            UpdateLibraryPathsInScope(Plugin.Instance.Options.IntroSkip.LibraryScope);
            UpdateUsersInScope(Plugin.Instance.Options.IntroSkip.UserScope);
        }

        public void UpdateLibraryPathsInScope(string currentScope)
        {
            var libraryIds = currentScope?.Split(new[] { ',', ';', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim()).ToArray();

            LibraryPathsInScope = libraryManager.GetVirtualFolders()
                .Where(f => libraryIds != null && libraryIds.Any()
                    ? libraryIds.Contains(f.ItemId)
                    : f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public void UpdateUsersInScope(string currentScope)
        {
            var userIds = currentScope
                ?.Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => long.TryParse(p.Trim(), out var id) ? (long?)id : null)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .ToArray();

            var userQuery = new UserQuery
            {
                IsDisabled = false
            };

            if (userIds != null && userIds.Any())
            {
                userQuery.UserIds = userIds;
                UsersInScope = userManager.GetUserList(userQuery);
            }
            else
            {
                UsersInScope = Array.Empty<User>();
            }
        }


        public void Initialize()
        {
            Dispose();

            sessionManager.PlaybackStart += OnPlaybackStart;
            sessionManager.PlaybackProgress += OnPlaybackProgress;
            sessionManager.PlaybackStopped += OnPlaybackStopped;
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (!(e.Item is Episode) || !e.PlaybackPositionTicks.HasValue)
            {
                return;
            }

            playSessionData.TryRemove(e.PlaySessionId, out _);
            var data = GetPlaySessionData(e);
            if (data == null)
            {
                logger.Info("IntroSkip - 不在范围内，跳过检测");
                return;
            }

            data.PlaybackStartTicks = e.PlaybackPositionTicks.Value;
            data.PreviousPositionTicks = e.PlaybackPositionTicks.Value;
            data.PreviousEventTime = DateTime.UtcNow;

            logger.Info("IntroSkip - 开始检测");
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            if (!(e.Item is Episode episode) ||
                (e.EventName != ProgressEvent.TimeUpdate &&
                 e.EventName != ProgressEvent.Unpause &&
                 e.EventName != ProgressEvent.PlaybackRateChange &&
                 e.EventName != ProgressEvent.Pause) ||
                !e.PlaybackPositionTicks.HasValue || e.PlaybackPositionTicks.Value == 0)
            {
                return;
            }

            var data = GetPlaySessionData(e);
            if (data == null)
            {
                return;
            }

            var currentPositionTicks = e.PlaybackPositionTicks.Value;
            var currentEventTime = DateTime.UtcNow;
            var introStart = data.IntroStart;
            var introEnd = data.IntroEnd;

            if (e.EventName == ProgressEvent.TimeUpdate && !introEnd.HasValue)
            {
                var elapsedTime = (currentEventTime - data.PreviousEventTime).TotalSeconds;
                var positionTimeDiff = TimeSpan.FromTicks(currentPositionTicks - data.PreviousPositionTicks).TotalSeconds;

                if (Math.Abs(positionTimeDiff) - elapsedTime > 5 &&
                    currentPositionTicks < data.MaxIntroDurationTicks)
                {
                    if (!data.FirstJumpPositionTicks.HasValue &&
                        TimeSpan.FromTicks(data.PlaybackStartTicks).TotalSeconds < 5 &&
                        positionTimeDiff > 0)
                    {
                        data.FirstJumpPositionTicks = data.PreviousPositionTicks;
                        if (data.PreviousPositionTicks > data.MinOpeningPlotDurationTicks)
                        {
                            data.MaxIntroDurationTicks += data.PreviousPositionTicks;
                        }
                    }

                    data.LastJumpPositionTicks = currentPositionTicks;
                }

                if (currentPositionTicks >= data.MaxIntroDurationTicks &&
                    data.LastJumpPositionTicks.HasValue)
                {
                    var introStartTicks = data.FirstJumpPositionTicks.HasValue &&
                                          data.FirstJumpPositionTicks.Value > data.MinOpeningPlotDurationTicks
                        ? data.FirstJumpPositionTicks.Value
                        : 0;

                    UpdateIntroTask(episode, e.Session, data, introStartTicks, data.LastJumpPositionTicks.Value);
                }

                data.PreviousPositionTicks = currentPositionTicks;
                data.PreviousEventTime = currentEventTime;
            }

            if (e.EventName == ProgressEvent.Pause)
            {
                data.LastPauseEventTime = currentEventTime;
                return;
            }

            if (e.EventName == ProgressEvent.PlaybackRateChange)
            {
                data.LastPlaybackRateChangeEventTime = currentEventTime;
                return;
            }

            if (e.EventName == ProgressEvent.Unpause && data.LastPauseEventTime.HasValue &&
                (currentEventTime - data.LastPauseEventTime.Value).TotalMilliseconds <
                (data.LastPlaybackRateChangeEventTime.HasValue ? 1500 : 500))
            {
                data.LastPauseEventTime = null;
                return;
            }

            if (e.EventName == ProgressEvent.Unpause && data.LastPauseEventTime.HasValue &&
                (currentEventTime - data.LastPauseEventTime.Value).TotalMilliseconds > 500 &&
                (currentEventTime - data.LastPauseEventTime.Value).TotalMilliseconds < 5000 &&
                introStart.HasValue && introStart.Value < currentPositionTicks && introEnd.HasValue &&
                currentPositionTicks < Math.Max(data.MaxIntroDurationTicks, introEnd.Value) &&
                Math.Abs(TimeSpan.FromTicks(currentPositionTicks - introEnd.Value).TotalMilliseconds) >
                (data.LastPlaybackRateChangeEventTime.HasValue ? 500 : 0))
            {
                UpdateIntroTask(episode, e.Session, data, introStart.Value, currentPositionTicks);
            }

            if (e.EventName == ProgressEvent.Unpause && episode.RunTimeTicks.HasValue &&
                data.LastPauseEventTime.HasValue &&
                (currentEventTime - data.LastPauseEventTime.Value).TotalMilliseconds > 500 &&
                (currentEventTime - data.LastPauseEventTime.Value).TotalMilliseconds < 5000 &&
                currentPositionTicks > episode.RunTimeTicks - data.MaxCreditsDurationTicks)
            {
                if (episode.RunTimeTicks.Value > currentPositionTicks)
                {
                    UpdateCreditsTask(episode, e.Session, data,
                        episode.RunTimeTicks.Value - currentPositionTicks);
                }
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue || !episode.RunTimeTicks.HasValue)
            {
                return;
            }

            var data = GetPlaySessionData(e);
            if (data == null)
            {
                return;
            }

            if (!data.CreditsStart.HasValue)
            {
                var currentPositionTicks = e.PlaybackPositionTicks.Value;
                if (currentPositionTicks > episode.RunTimeTicks - data.MaxCreditsDurationTicks)
                {
                    if (episode.RunTimeTicks.Value > currentPositionTicks)
                    {
                        UpdateCreditsTask(episode, e.Session, data,
                            episode.RunTimeTicks.Value - currentPositionTicks);
                    }
                }
            }

            playSessionData.TryRemove(e.PlaySessionId, out _);
            lastIntroUpdateTimes.TryRemove(episode, out _);
            lastCreditsUpdateTimes.TryRemove(episode, out _);
        }

        private IntroSkipPlaySessionData GetPlaySessionData(PlaybackProgressEventArgs e)
        {
            if (!IsLibraryInScope(e.Item) || !IsUserInScope(e.Session.UserInternalId))
            {
                return null;
            }

            if (!playSessionData.ContainsKey(e.PlaySessionId))
            {
                playSessionData[e.PlaySessionId] = new IntroSkipPlaySessionData(e.Item);
            }

            return playSessionData[e.PlaySessionId];
        }

        private IntroSkipPlaySessionData GetPlaySessionData(PlaybackStopEventArgs e)
        {
            if (!IsLibraryInScope(e.Item) || !IsUserInScope(e.Session.UserInternalId))
            {
                return null;
            }

            if (!playSessionData.ContainsKey(e.PlaySessionId))
            {
                playSessionData[e.PlaySessionId] = new IntroSkipPlaySessionData(e.Item);
            }

            return playSessionData[e.PlaySessionId];
        }

        private bool IsLibraryInScope(BaseItem item)
        {
            if (string.IsNullOrEmpty(item.ContainingFolderPath))
            {
                return false;
            }

            return LibraryPathsInScope.Any(l => item.ContainingFolderPath.StartsWith(l, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsUserInScope(long userInternalId)
        {
            if (!UsersInScope.Any())
            {
                return true;
            }

            return UsersInScope.Any(u => u.InternalId == userInternalId);
        }

        private void UpdateIntroTask(Episode episode, SessionInfo session, IntroSkipPlaySessionData data,
            long introStartPositionTicks, long introEndPositionTicks)
        {
            var now = DateTime.UtcNow;

            lock (introLock)
            {
                if (ongoingIntroUpdates.ContainsKey(episode))
                {
                    return;
                }

                if (lastIntroUpdateTimes.TryGetValue(episode, out var lastUpdateTime))
                {
                    if (now - lastUpdateTime < updateInterval)
                    {
                        return;
                    }
                }

                var task = new Task(() =>
                {
                    try
                    {
                        Plugin.IntroSkipChapterApi.UpdateIntro(episode, session, introStartPositionTicks,
                            introEndPositionTicks);
                        data.IntroStart = Plugin.IntroSkipChapterApi.GetIntroStart(episode);
                        data.IntroEnd = Plugin.IntroSkipChapterApi.GetIntroEnd(episode);
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex.Message);
                        logger.Debug(ex.StackTrace);
                    }
                });

                if (ongoingIntroUpdates.TryAdd(episode, task))
                {
                    task.ContinueWith(t => { ongoingIntroUpdates.TryRemove(episode, out _); },
                        TaskContinuationOptions.ExecuteSynchronously);
                    lastIntroUpdateTimes[episode] = now;
                    task.Start();
                }
            }
        }

        private void UpdateCreditsTask(Episode episode, SessionInfo session, IntroSkipPlaySessionData data,
            long creditsDurationTicks)
        {
            var now = DateTime.UtcNow;

            lock (creditsLock)
            {
                if (ongoingCreditsUpdates.ContainsKey(episode))
                {
                    return;
                }

                if (lastCreditsUpdateTimes.TryGetValue(episode, out var lastUpdateTime))
                {
                    if (now - lastUpdateTime < updateInterval)
                    {
                        return;
                    }
                }

                var task = new Task(() =>
                {
                    try
                    {
                        Plugin.IntroSkipChapterApi.UpdateCredits(episode, session, creditsDurationTicks);
                        data.CreditsStart = Plugin.IntroSkipChapterApi.GetCreditsStart(episode);
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex.Message);
                        logger.Debug(ex.StackTrace);
                    }
                });

                if (ongoingCreditsUpdates.TryAdd(episode, task))
                {
                    task.ContinueWith(t => { ongoingCreditsUpdates.TryRemove(episode, out _); },
                        TaskContinuationOptions.ExecuteSynchronously);
                    lastCreditsUpdateTimes[episode] = now;
                    task.Start();
                }
            }
        }

        public void Dispose()
        {
            sessionManager.PlaybackStart -= OnPlaybackStart;
            sessionManager.PlaybackProgress -= OnPlaybackProgress;
            sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
    }
}
