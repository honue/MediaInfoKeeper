namespace MediaInfoKeeper.Options.View
{
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Configuration;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;

    internal class GitHubPageView : PluginPageView
    {
        private readonly GitHubOptionsStore store;

        public GitHubPageView(PluginInfo pluginInfo, GitHubOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
        }

        public GitHubOptions Options => this.ContentData as GitHubOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.store.SetOptions(this.Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
