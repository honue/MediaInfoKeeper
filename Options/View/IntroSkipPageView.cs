namespace MediaInfoKeeper.Options.View
{
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Configuration;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;

    internal class IntroSkipPageView : PluginPageView
    {
        private readonly IntroSkipOptionsStore store;

        public IntroSkipPageView(PluginInfo pluginInfo, IntroSkipOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.store = store;
            this.ContentData = store.GetOptions();
        }

        public IntroSkipOptions Options => this.ContentData as IntroSkipOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.store.SetOptions(this.Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}
