using System;
using System.Text.Json.Nodes;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using MediaInfoKeeper.Options.UIBaseClasses.Store;

namespace MediaInfoKeeper.Options.Store {
    internal class PluginOptionsStore : SimpleFileStore<PluginConfiguration> {
        private readonly Action<PluginConfiguration> onSaved;
        private readonly Func<PluginConfiguration, bool> onSaving;
        private readonly Action<PluginConfiguration> prepareForUi;

        public PluginOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName,
            Action<PluginConfiguration> prepareForUi,
            Func<PluginConfiguration, bool> onSaving,
            Action<PluginConfiguration> onSaved)
            : base(applicationHost, logger, pluginFullName) {
            this.prepareForUi = prepareForUi;
            this.onSaving = onSaving;
            this.onSaved = onSaved;

            FileSaving += HandleFileSaving;
            FileSaved += HandleFileSaved;
        }

        public PluginConfiguration GetOptionsForUi() {
            var options = GetOptions();
            prepareForUi?.Invoke(options);
            return options;
        }

        public new void SetOptionsSilently(PluginConfiguration options) {
            base.SetOptionsSilently(options);
        }

        protected override JsonNode TransformLoadedJson(JsonNode rootNode) {
            if (!(rootNode is JsonObject root)) return rootNode;

            var mainPage = GetOrCreateObject(root, nameof(PluginConfiguration.MainPage));
            var itemAddedTaskEditor = GetOrCreateObject(mainPage, nameof(MainPageOptions.ItemAddedTaskEditor));
            CopyString(mainPage, itemAddedTaskEditor, "ItemAddedMediaInfoLibraries",
                nameof(MainPageOptions.ItemAddedTaskEditorOptions.ItemAddedMediaInfoLibraries));
            CopyString(mainPage, itemAddedTaskEditor, "ItemAddedRefreshMetadataLibraries",
                nameof(MainPageOptions.ItemAddedTaskEditorOptions.ItemAddedRefreshMetadataLibraries));
            CopyString(mainPage, itemAddedTaskEditor, "ItemAddedIntroScanLibraries",
                nameof(MainPageOptions.ItemAddedTaskEditorOptions.ItemAddedIntroScanLibraries));

            var scheduledTasksEditor = GetOrCreateObject(mainPage, nameof(MainPageOptions.ScheduledTasksEditor));

            var refreshRecentMetadata = GetOrCreateObject(scheduledTasksEditor,
                nameof(MainPageOptions.ScheduledTaskEditorOptions.RefreshRecentMetadata));
            CopyInt(mainPage, refreshRecentMetadata, "RefreshRecentMetadataDays",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.RefreshRecentMetadataDays));
            CopyString(mainPage, refreshRecentMetadata, "RefreshRecentMetadataLibraries",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.RefreshRecentMetadataLibraries));
            CopyValue(mainPage, refreshRecentMetadata, "RefreshMetadataMode",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.RefreshMetadataMode));
            CopyValue(mainPage, refreshRecentMetadata, "ReplaceExistingImages",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.ReplaceExistingImages));
            CopyValue(mainPage, refreshRecentMetadata, "ReplaceExistingVideoPreviewThumbnails",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.ReplaceExistingVideoPreviewThumbnails));
            CopyValue(mainPage, refreshRecentMetadata, "AllowFfProcess",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.AllowFfProcess));
            CopyValue(mainPage, refreshRecentMetadata, "EnablePremiereDateFilter",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.EnablePremiereDateFilter));
            CopyValue(mainPage, refreshRecentMetadata, "RefreshCompletedSeriesEpisodes",
                nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.RefreshCompletedSeriesEpisodes));

            var submitTheIntroDbMarkers = GetOrCreateObject(scheduledTasksEditor,
                nameof(MainPageOptions.ScheduledTaskEditorOptions.SubmitTheIntroDbMarkers));
            CopyInt(mainPage, submitTheIntroDbMarkers, "SubmitTheIntroDbMarkersDays",
                nameof(MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions.SubmitTheIntroDbMarkersDays));
            CopyString(mainPage, submitTheIntroDbMarkers, "SubmitTheIntroDbMarkersLibraries",
                nameof(MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions.SubmitTheIntroDbMarkersLibraries));

            var exportExistingMediaInfo = GetOrCreateObject(scheduledTasksEditor,
                nameof(MainPageOptions.ScheduledTaskEditorOptions.ExportExistingMediaInfo));
            CopyString(mainPage, exportExistingMediaInfo, "ExportExistingMediaInfoLibraries",
                nameof(MainPageOptions.ExportExistingMediaInfoTaskEditorOptions.ExportExistingMediaInfoLibraries));

            var restoreMediaInfo = GetOrCreateObject(scheduledTasksEditor,
                nameof(MainPageOptions.ScheduledTaskEditorOptions.RestoreMediaInfo));
            CopyString(mainPage, restoreMediaInfo, "RestoreMediaInfoLibraries",
                nameof(MainPageOptions.RestoreMediaInfoTaskEditorOptions.RestoreMediaInfoLibraries));

            var updatePlugin = GetOrCreateObject(scheduledTasksEditor,
                nameof(MainPageOptions.ScheduledTaskEditorOptions.UpdatePlugin));
            if (root[nameof(PluginConfiguration.GitHub)] is JsonObject gitHub) {
                if (gitHub[nameof(GitHubOptions.GitHubToken)] is JsonValue gitHubTokenValue &&
                    gitHubTokenValue.TryGetValue<string>(out var gitHubToken) &&
                    !string.IsNullOrWhiteSpace(gitHubToken))
                    updatePlugin[nameof(MainPageOptions.UpdatePluginTaskEditorOptions.GitHubToken)] = gitHubToken;

                if (gitHub[nameof(GitHubOptions.DownloadUrlPrefix)] is JsonValue downloadUrlPrefixValue &&
                    downloadUrlPrefixValue.TryGetValue<string>(out var downloadUrlPrefix) &&
                    !string.IsNullOrWhiteSpace(downloadUrlPrefix))
                    updatePlugin[nameof(MainPageOptions.UpdatePluginTaskEditorOptions.DownloadUrlPrefix)] =
                        downloadUrlPrefix;

                if (gitHub[nameof(GitHubOptions.UpdateChannel)] is JsonValue updateChannelValue &&
                    updateChannelValue.TryGetValue<string>(out var updateChannel) &&
                    !string.IsNullOrWhiteSpace(updateChannel))
                    updatePlugin[nameof(MainPageOptions.UpdatePluginTaskEditorOptions.UpdateChannel)] = updateChannel;
            }

            return root;
        }

        protected override JsonNode TransformSavingJson(JsonNode rootNode, PluginConfiguration options) {
            if (!(rootNode is JsonObject root)) return rootNode;

            if (root[nameof(PluginConfiguration.MainPage)] is JsonObject mainPage)
                Remove(mainPage,
                    "ItemAddedMediaInfoLibraries",
                    "ItemAddedRefreshMetadataLibraries",
                    "ItemAddedIntroScanLibraries",
                    "RefreshRecentMetadataDays",
                    "RefreshRecentMetadataLibraries",
                    "RefreshMetadataMode",
                    "ReplaceExistingImages",
                    "ReplaceExistingVideoPreviewThumbnails",
                    "AllowFfProcess",
                    "EnablePremiereDateFilter",
                    "RefreshCompletedSeriesEpisodes",
                    "ScanRecentIntroLimit",
                    "ScanRecentIntroLibraries",
                    "SubmitTheIntroDbMarkersLimit",
                    "SubmitTheIntroDbMarkersDays",
                    "SubmitTheIntroDbMarkersLibraries",
                    "ExtractRecentMediaInfoLimit",
                    "ExtractRecentMediaInfoLibraries",
                    "ExportExistingMediaInfoLibraries",
                    "RestoreMediaInfoLibraries",
                    "ScanExternalFilesLibraries");

            root.Remove(nameof(PluginConfiguration.GitHub));
            return root;
        }

        private void HandleFileSaving(object sender, FileSavingEventArgs e) {
            if (onSaving == null) return;

            if (e.Options is PluginConfiguration options && !onSaving(options)) e.Cancel = true;
        }

        private void HandleFileSaved(object sender, FileSavedEventArgs e) {
            if (onSaved == null) return;

            if (e.Options is PluginConfiguration options) onSaved(options);
        }

        private static JsonObject GetOrCreateObject(JsonObject parent, string name) {
            if (!(parent[name] is JsonObject child)) {
                child = new JsonObject();
                parent[name] = child;
            }

            return child;
        }

        private static void CopyString(JsonObject source, JsonObject target, string sourceName, string targetName) {
            if (source == null || target == null || target[targetName] != null) return;

            if (source[sourceName] is JsonValue value && value.TryGetValue<string>(out var text) &&
                !string.IsNullOrWhiteSpace(text))
                target[targetName] = text;
        }

        private static void CopyInt(JsonObject source, JsonObject target, string sourceName, string targetName) {
            if (source == null || target == null || target[targetName] != null) return;

            if (source[sourceName] is JsonValue value && value.TryGetValue<int>(out var number)) target[targetName] = number;
        }

        private static void CopyValue(JsonObject source, JsonObject target, string sourceName, string targetName) {
            if (source == null || target == null || target[targetName] != null) return;

            if (source[sourceName] != null) target[targetName] = JsonNode.Parse(source[sourceName].ToJsonString());
        }

        private static void Remove(JsonObject jsonObject, params string[] propertyNames) {
            foreach (var propertyName in propertyNames) jsonObject.Remove(propertyName);
        }
    }
}
