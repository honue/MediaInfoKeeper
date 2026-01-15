namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Configuration;

    internal class MainPageOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public MainPageOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public MainPageOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return new MainPageOptions
            {
                General = options.General ?? new GeneralOptions(),
                LibraryScope = options.LibraryScope ?? new LibraryScopeOptions(),
                RecentTasks = options.RecentTasks ?? new RecentTaskOptions()
            };
        }

        public void SetOptions(MainPageOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.General = options?.General ?? new GeneralOptions();
            pluginOptions.LibraryScope = options?.LibraryScope ?? new LibraryScopeOptions();
            pluginOptions.RecentTasks = options?.RecentTasks ?? new RecentTaskOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
