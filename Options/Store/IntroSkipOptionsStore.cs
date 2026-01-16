namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Configuration;

    internal class IntroSkipOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public IntroSkipOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public IntroSkipOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return options.IntroSkip ?? new IntroSkipOptions();
        }

        public void SetOptions(IntroSkipOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.IntroSkip = options ?? new IntroSkipOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
