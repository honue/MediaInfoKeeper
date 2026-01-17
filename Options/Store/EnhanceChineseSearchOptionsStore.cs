namespace MediaInfoKeeper.Options.Store
{
    using System;
    using MediaInfoKeeper.Configuration;
    using MediaInfoKeeper.Services;

    internal class EnhanceChineseSearchOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public EnhanceChineseSearchOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public EnhanceChineseSearchOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            var searchOptions = options.EnhanceChineseSearch ?? new EnhanceChineseSearchOptions();
            searchOptions.Initialize();
            return searchOptions;
        }

        public void SetOptions(EnhanceChineseSearchOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            var current = pluginOptions.EnhanceChineseSearch ?? new EnhanceChineseSearchOptions();
            var next = options ?? new EnhanceChineseSearchOptions();

            if (!string.Equals(current.SearchScope, next.SearchScope, StringComparison.Ordinal))
            {
                SearchScopeUtility.UpdateSearchScope(next.SearchScope);
            }

            var isSimpleTokenizer =
                string.Equals(EnhanceChineseSearch.CurrentTokenizerName, "simple", StringComparison.Ordinal);
            next.EnhanceChineseSearchRestore = !next.EnhanceChineseSearch && isSimpleTokenizer;

            pluginOptions.EnhanceChineseSearch = next;
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }
    }
}
