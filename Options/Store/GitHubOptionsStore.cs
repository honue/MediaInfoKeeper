namespace MediaInfoKeeper.Options.Store
{
    using MediaInfoKeeper.Configuration;

    internal class GitHubOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public GitHubOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public GitHubOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            return options.GitHub ?? new GitHubOptions();
        }

        public void SetOptions(GitHubOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            pluginOptions.GitHub = options ?? new GitHubOptions();
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
