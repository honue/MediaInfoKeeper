using System;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.UIBaseClasses.Views;
using MediaInfoKeeper.ScheduledTask;

namespace MediaInfoKeeper.Options.View {
    internal class MainPageView : PluginPageView {
        private const string ItemAddedTaskDialogCommandId = "main.itemAdded.configure";
        private const string UpdatePluginDialogCommandId = "main.scheduled.updatePlugin";
        private const string UpdatePluginRunCommandId = "main.scheduled.run.updatePlugin";
        private const string RefreshRecentMetadataDialogCommandId = "main.scheduled.refreshRecentMetadata";
        private const string RefreshRecentMetadataRunCommandId = "main.scheduled.run.refreshRecentMetadata";
        private const string SubmitTheIntroDbMarkersDialogCommandId = "main.scheduled.submitTheIntroDbMarkers";
        private const string SubmitTheIntroDbMarkersRunCommandId = "main.scheduled.run.submitTheIntroDbMarkers";
        private const string ExportExistingMediaInfoDialogCommandId = "main.scheduled.exportExistingMediaInfo";
        private const string ExportExistingMediaInfoRunCommandId = "main.scheduled.run.exportExistingMediaInfo";
        private const string RestoreMediaInfoDialogCommandId = "main.scheduled.restoreMediaInfo";
        private const string RestoreMediaInfoRunCommandId = "main.scheduled.run.restoreMediaInfo";
        private const string RestartEmbyRunCommandId = "main.scheduled.run.restartEmby";
        private const string ExtractAllMediaInfoRunCommandId = "main.scheduled.run.extractAllMediaInfo";
        private const string ScanAllIntroRunCommandId = "main.scheduled.run.scanAllIntro";
        private const string ExtractAllMediaInfoDialogCommandId = "main.scheduled.extractAllMediaInfo";
        private const string ScanAllIntroDialogCommandId = "main.scheduled.scanAllIntro";
        private const string GenerateThumbnailsDialogCommandId = "main.scheduled.generateThumbnails";
        private const string GenerateThumbnailsRunCommandId = "main.scheduled.run.generateThumbnails";
        private const string RefreshQueueStatusCommandId = "main.refreshQueueStatus";

        private readonly IApplicationHost applicationHost;
        private readonly PluginInfo pluginInfo;
        private readonly MainPageOptionsStore store;

        public MainPageView(IApplicationHost applicationHost, PluginInfo pluginInfo, MainPageOptionsStore store)
            : base(pluginInfo.Id) {
            this.applicationHost = applicationHost;
            this.pluginInfo = pluginInfo;
            this.store = store;
            ContentData = store.GetOptions();
        }

        public MainPageOptions Options => ContentData as MainPageOptions;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data) {
            if (string.Equals(commandId, ItemAddedTaskDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new ItemAddedTaskDialogView(pluginInfo.Id, Options));

            if (string.Equals(commandId, UpdatePluginDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new UpdatePluginTaskDialogView(pluginInfo.Id, Options));

            if (string.Equals(commandId, RefreshRecentMetadataDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new RefreshRecentMetadataTaskDialogView(pluginInfo.Id, Options));

            if (string.Equals(commandId, SubmitTheIntroDbMarkersDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(
                    new SubmitTheIntroDbMarkersTaskDialogView(pluginInfo.Id, Options));

            if (string.Equals(commandId, ExportExistingMediaInfoDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(
                    new ExportExistingMediaInfoTaskDialogView(pluginInfo.Id, Options));

            if (string.Equals(commandId, RestoreMediaInfoDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new RestoreMediaInfoTaskDialogView(pluginInfo.Id, Options));

            if (string.Equals(commandId, ExtractAllMediaInfoDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(
                    new LibraryScopeTaskDialogView(pluginInfo.Id, Options, "extract", "补齐缺失的媒体信息"));

            if (string.Equals(commandId, ScanAllIntroDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(
                    new LibraryScopeTaskDialogView(pluginInfo.Id, Options, "intro", "补齐缺失的片头片尾标记"));

            if (string.Equals(commandId, GenerateThumbnailsDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(
                    new LibraryScopeTaskDialogView(pluginInfo.Id, Options, "thumbnail", "补齐缺失的视频预览缩略图"));

            if (string.Equals(commandId, RefreshQueueStatusCommandId, StringComparison.Ordinal)) {
                Options.RefreshQueueStatus = MainPageOptionsStore.BuildRefreshQueueStatus();
                return Task.FromResult<IPluginUIView>(this);
            }

            if (string.Equals(commandId, UpdatePluginRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<UpdatePluginTask>();

            if (string.Equals(commandId, RefreshRecentMetadataRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<RefreshRecentMetadataTask>();

            if (string.Equals(commandId, SubmitTheIntroDbMarkersRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<SubmitMarkersTask>();

            if (string.Equals(commandId, ExportExistingMediaInfoRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<ExportExistingMediaInfoTask>();

            if (string.Equals(commandId, RestoreMediaInfoRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<RestoreMediaInfoTask>();

            if (string.Equals(commandId, RestartEmbyRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<RestartEmbyTask>();

            if (string.Equals(commandId, ExtractAllMediaInfoRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<ExtractAllMediaInfoTask>();

            if (string.Equals(commandId, ScanAllIntroRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<ScanAllIntroTask>();

            if (string.Equals(commandId, GenerateThumbnailsRunCommandId, StringComparison.Ordinal))
                return RunScheduledTaskAsync<GenerateThumbnailsTask>();

            return base.RunCommand(itemId, commandId, data);
        }

        public override async Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data) {
            store.SetOptions(Options);
            return await base.OnSaveCommand(itemId, commandId, data).ConfigureAwait(false);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) {
            base.OnDialogResult(dialogView, completedOk, data);
            if (!completedOk) return;

            store.SetOptions(Options);
        }

        private async Task<IPluginUIView> RunScheduledTaskAsync<TTask>()
            where TTask : IScheduledTask {
            var taskManager = applicationHost.Resolve<ITaskManager>();
            if (taskManager == null) {
                if (Plugin.NotificationApi != null)
                    await Plugin.NotificationApi.DisplayMessage(User, "无法启动任务，请查看 Emby 日志").ConfigureAwait(false);
                return this;
            }

            taskManager.QueueScheduledTask<TTask>(new TaskOptions {
                HasManualInteraction = true
            });
            if (Plugin.NotificationApi != null)
                await Plugin.NotificationApi.DisplayMessage(User, "任务已启动，可在 Emby 计划任务中查看进度").ConfigureAwait(false);
            Options.RefreshQueueStatus = MainPageOptionsStore.BuildRefreshQueueStatus();
            return this;
        }
    }
}
