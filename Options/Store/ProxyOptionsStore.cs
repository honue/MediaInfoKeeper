namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Configuration;

    internal class ProxyOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public ProxyOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public ProxyOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return options.Proxy ?? new ProxyOptions();
        }

        public void SetOptions(ProxyOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.Proxy = options ?? new ProxyOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
