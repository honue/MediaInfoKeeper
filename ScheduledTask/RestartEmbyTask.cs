using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace MediaInfoKeeper.ScheduledTask
{
    public class RestartEmbyTask : IScheduledTask
    {
        private readonly IApplicationHost applicationHost;
        private readonly ILogger logger;

        public RestartEmbyTask(IApplicationHost applicationHost, ILogManager logManager)
        {
            this.applicationHost = applicationHost;
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperRestartEmbyTask";

        public string Name => "09.重启Emby";

        public string Description => "重启 Emby，临时释放内存。";

        public string Category => Plugin.TaskCategoryName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(0);

            if (!this.applicationHost.CanSelfRestart)
            {
                logger.Error("当前 Emby 环境不支持自重启，请手动重启服务。");
                return Task.CompletedTask;
            }

            this.logger.Info("重启 Emby 计划任务开始，Emby 正在自重启。");
            progress?.Report(100);
            this.applicationHost.Restart();

            return Task.CompletedTask;
        }
    }
}
