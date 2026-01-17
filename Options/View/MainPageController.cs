namespace MediaInfoKeeper.Options.View
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses;

    internal class MainPageController : ControllerBase, IHasTabbedUIPages
    {
        private readonly PluginInfo pluginInfo;
        private readonly MainPageOptionsStore mainPageOptionsStore;
        private readonly List<IPluginUIPageController> tabPages = new List<IPluginUIPageController>();

        public MainPageController(PluginInfo pluginInfo,
            MainPageOptionsStore mainPageOptionsStore,
            GitHubOptionsStore gitHubOptionsStore,
            IntroSkipOptionsStore introSkipOptionsStore,
            ProxyOptionsStore proxyOptionsStore,
            EnhanceChineseSearchOptionsStore enhanceChineseSearchOptionsStore)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.mainPageOptionsStore = mainPageOptionsStore;

            this.PageInfo = new PluginPageInfo
            {
                Name = "MediaInfoKeeper",
                EnableInMainMenu = true,
                DisplayName = "MediaInfo Keeper",
                MenuIcon = "video_settings",
                IsMainConfigPage = true
            };


            this.tabPages.Add(new TabPageController(pluginInfo, nameof(IntroSkipPageView), "IntroSkip",
                e => new IntroSkipPageView(pluginInfo, introSkipOptionsStore)));

            this.tabPages.Add(new TabPageController(pluginInfo, nameof(EnhanceChineseSearchPageView), "Search",
                e => new EnhanceChineseSearchPageView(pluginInfo, enhanceChineseSearchOptionsStore)));

            this.tabPages.Add(new TabPageController(pluginInfo, nameof(ProxyPageView), "Proxy",
                e => new ProxyPageView(pluginInfo, proxyOptionsStore)));

            this.tabPages.Add(new TabPageController(pluginInfo, nameof(GitHubPageView), "GitHub & Update",
                e => new GitHubPageView(pluginInfo, gitHubOptionsStore)));

        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new MainPageView(this.pluginInfo, this.mainPageOptionsStore);
            return Task.FromResult(view);
        }

        public IReadOnlyList<IPluginUIPageController> TabPageControllers => this.tabPages.AsReadOnly();
    }
}
