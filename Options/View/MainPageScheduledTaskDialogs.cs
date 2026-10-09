using System.Threading.Tasks;

namespace MediaInfoKeeper.Options.View {
    internal sealed class
        UpdatePluginTaskDialogView : MainPageTaskDialogView<MainPageOptions.UpdatePluginTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public UpdatePluginTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.UpdatePlugin ?? new MainPageOptions.UpdatePluginTaskEditorOptions(),
                "更新插件") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.UpdatePlugin = Options;
        }
    }

    internal sealed class
        RefreshRecentMetadataTaskDialogView : MainPageTaskDialogView<
        MainPageOptions.RefreshRecentMetadataTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public RefreshRecentMetadataTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.RefreshRecentMetadata ??
                new MainPageOptions.RefreshRecentMetadataTaskEditorOptions(), "刷新媒体元数据") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.RefreshRecentMetadata = Options;
        }
    }

    internal sealed class
        SubmitTheIntroDbMarkersTaskDialogView : MainPageTaskDialogView<
        MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public SubmitTheIntroDbMarkersTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.SubmitTheIntroDbMarkers ??
                new MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions(), "共享片头片尾") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.SubmitTheIntroDbMarkers = Options;
        }
    }

    internal sealed class
        ExportExistingMediaInfoTaskDialogView : MainPageTaskDialogView<
        MainPageOptions.ExportExistingMediaInfoTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public ExportExistingMediaInfoTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.ExportExistingMediaInfo ??
                new MainPageOptions.ExportExistingMediaInfoTaskEditorOptions(), "备份媒体信息") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.ExportExistingMediaInfo = Options;
        }
    }

    internal sealed class
        LibraryScopeTaskDialogView : MainPageTaskDialogView<MainPageOptions.LibraryScopeTaskEditorOptions> {
        private readonly MainPageOptions owner;
        private readonly string task;

        public LibraryScopeTaskDialogView(string pluginId, MainPageOptions owner, string task, string caption)
            : base(pluginId, GetOptions(owner, task), caption) {
            this.owner = owner;
            this.task = task;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor == null) return;

            if (task == "extract") owner.ScheduledTasksEditor.ExtractAllMediaInfo = Options;
            else if (task == "intro") owner.ScheduledTasksEditor.ScanAllIntro = Options;
            else owner.ScheduledTasksEditor.GenerateThumbnails = Options;
        }

        private static MainPageOptions.LibraryScopeTaskEditorOptions GetOptions(MainPageOptions owner, string task) {
            var editor = owner?.ScheduledTasksEditor;
            if (task == "extract") return editor?.ExtractAllMediaInfo ?? new MainPageOptions.LibraryScopeTaskEditorOptions();
            if (task == "intro") return editor?.ScanAllIntro ?? new MainPageOptions.LibraryScopeTaskEditorOptions();
            return editor?.GenerateThumbnails ?? new MainPageOptions.LibraryScopeTaskEditorOptions();
        }
    }

    internal sealed class
        RestoreMediaInfoTaskDialogView : MainPageTaskDialogView<MainPageOptions.RestoreMediaInfoTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public RestoreMediaInfoTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.RestoreMediaInfo ??
                new MainPageOptions.RestoreMediaInfoTaskEditorOptions(), "恢复媒体信息") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.RestoreMediaInfo = Options;
        }
    }

}
