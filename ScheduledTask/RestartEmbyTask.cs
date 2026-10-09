using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask {
    public class RestartEmbyTask : IScheduledTask {
        private static readonly object DelayedCheckTimerLock = new();
        private static readonly TimeSpan DelayedCheckDelay = TimeSpan.FromMinutes(30);
        private static Timer delayedCheckTimer;

        private readonly IApplicationHost applicationHost;
        private readonly ILiveTvManager liveTvManager;
        private readonly ILogger logger;
        private readonly ISessionManager sessionManager;
        private readonly ITaskManager taskManager;

        public RestartEmbyTask(
            IApplicationHost applicationHost,
            ILogManager logManager,
            ILiveTvManager liveTvManager,
            ISessionManager sessionManager,
            ITaskManager taskManager) {
            this.applicationHost = applicationHost;
            this.liveTvManager = liveTvManager;
            this.sessionManager = sessionManager;
            this.taskManager = taskManager;
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperRestartEmbyTask";

        public string Name => "07.安全重启 Emby";

        public string Description => "在没有用户播放且没有 Live TV 录制时重启 Emby；否则会延后 30 分钟再检查。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0);

            if (!applicationHost.CanSelfRestart) {
                logger.Error("当前 Emby 环境不支持自重启，请手动重启服务。");
                return;
            }

            await Task.Yield();

            var restartStatus = RestartReadinessChecker.GetStatus(sessionManager, liveTvManager, logger);
            if (!restartStatus.CanRestart) {
                ScheduleDelayedCheck(taskManager, logger, restartStatus);
                progress?.Report(100);
                return;
            }

            logger.Info("重启 Emby 计划任务开始，Emby 正在自重启。");
            progress?.Report(100);
            applicationHost.Restart();
        }

        internal static void ScheduleDelayedCheck(
            ITaskManager taskManager,
            ILogger logger,
            RestartReadinessStatus restartStatus) {
            var worker = FindRestartTaskWorker(taskManager);
            if (worker == null) {
                logger.Warn("无法找到重启 Emby 计划任务，不能安排 30 分钟后重新检查。");
                return;
            }

            var nextCheckTime = DateTime.Now.Add(DelayedCheckDelay);
            lock (DelayedCheckTimerLock) {
                delayedCheckTimer?.Dispose();
                delayedCheckTimer = new Timer(_ => {
                    lock (DelayedCheckTimerLock) {
                        delayedCheckTimer?.Dispose();
                        delayedCheckTimer = null;
                    }

                    _ = taskManager.Execute(worker, new TaskOptions());
                }, null, DelayedCheckDelay, Timeout.InfiniteTimeSpan);
            }

            logger.Info(
                "{0}，已安排 {1:yyyy-MM-dd HH:mm:ss} 再次检查是否可以重启 Emby。",
                restartStatus?.Describe() ?? "检测到重启阻止条件",
                nextCheckTime);
        }

        private static IScheduledTaskWorker FindRestartTaskWorker(ITaskManager taskManager) {
            return taskManager?.ScheduledTasks.FirstOrDefault(worker =>
                string.Equals(worker?.ScheduledTask?.Key, "MediaInfoKeeperRestartEmbyTask", StringComparison.Ordinal));
        }
    }
}
