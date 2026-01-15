namespace MediaInfoKeeper.Options.Store
{
    using System;
    using MediaInfoKeeper.Configuration;
    using MediaInfoKeeper.Options.UIBaseClasses.Store;
    using MediaBrowser.Common;
    using MediaBrowser.Model.Logging;

    internal class PluginOptionsStore : SimpleFileStore<PluginConfiguration>
    {
        private readonly Action<PluginConfiguration> prepareForUi;
        private readonly Func<PluginConfiguration, bool> onSaving;
        private readonly Action<PluginConfiguration> onSaved;

        public PluginOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName,
            Action<PluginConfiguration> prepareForUi,
            Func<PluginConfiguration, bool> onSaving,
            Action<PluginConfiguration> onSaved)
            : base(applicationHost, logger, pluginFullName)
        {
            this.prepareForUi = prepareForUi;
            this.onSaving = onSaving;
            this.onSaved = onSaved;

            this.FileSaving += HandleFileSaving;
            this.FileSaved += HandleFileSaved;
        }

        public PluginConfiguration GetOptionsForUi()
        {
            var options = this.GetOptions();
            this.prepareForUi?.Invoke(options);
            return options;
        }

        private void HandleFileSaving(object sender, FileSavingEventArgs e)
        {
            if (this.onSaving == null)
            {
                return;
            }

            if (e.Options is PluginConfiguration options && !this.onSaving(options))
            {
                e.Cancel = true;
            }
        }

        private void HandleFileSaved(object sender, FileSavedEventArgs e)
        {
            if (this.onSaved == null)
            {
                return;
            }

            if (e.Options is PluginConfiguration options)
            {
                this.onSaved(options);
            }
        }
    }
}
