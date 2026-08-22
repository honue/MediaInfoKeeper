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

        private Task<IPluginUIView> RunScheduledTaskAsync<TTask>()
            where TTask : IScheduledTask {
            var taskManager = applicationHost.Resolve<ITaskManager>();
            taskManager?.QueueScheduledTask<TTask>(new TaskOptions {
                HasManualInteraction = true
            });

            return Task.FromResult<IPluginUIView>(this);
        }
    }
}
