namespace MediaInfoKeeper.Options.Store
{
    using System;
    using Emby.Web.GenericEdit.Elements;
    using MediaInfoKeeper.Patch;
    using MediaInfoKeeper.Options;

    internal class EnhanceOptionsStore
    {
        private readonly PluginOptionsStore pluginOptionsStore;

        public EnhanceOptionsStore(PluginOptionsStore pluginOptionsStore)
        {
            this.pluginOptionsStore = pluginOptionsStore;
        }

        public EnhanceOptions GetOptions()
        {
            var options = this.pluginOptionsStore.GetOptionsForUi();
            var enhanceOptions = options.Enhance ?? new EnhanceOptions();
            enhanceOptions.Initialize();
            enhanceOptions.ChineseSearchTokenizerStatus = BuildChineseSearchTokenizerStatus(enhanceOptions);
            enhanceOptions.ShowChineseSearchTokenizerStatus = true;
            return enhanceOptions;
        }

        public void SetOptions(EnhanceOptions options)
        {
            var pluginOptions = this.pluginOptionsStore.GetOptions();
            var current = pluginOptions.Enhance ?? new EnhanceOptions();
            var next = options ?? new EnhanceOptions();

            if (!string.Equals(current.SearchScope, next.SearchScope, StringComparison.Ordinal))
            {
                ChineseSearch.UpdateSearchScope(next.SearchScope);
            }

            if (current.MergeMultiVersion != next.MergeMultiVersion ||
                current.MergeSeriesPreference != next.MergeSeriesPreference)
            {
                var pluginEnabled = Plugin.Instance?.Options?.MainPage?.PlugginEnabled ?? true;
                MergeMultiVersion.Configure(
                    pluginEnabled && next.MergeMultiVersion,
                    next.MergeSeriesPreference);
            }

            if (next.MergeMultiVersion)
            {
                Plugin.LibraryService?.EnsureLibraryEnabledAutomaticSeriesGrouping();
            }

            var isSimpleTokenizer =
                string.Equals(ChineseSearch.CurrentTokenizerName, "simple", StringComparison.Ordinal);
            next.EnhanceChineseSearchRestore = !next.EnhanceChineseSearch && isSimpleTokenizer;
            next.Initialize();
            next.ChineseSearchTokenizerStatus = BuildChineseSearchTokenizerStatus(next);
            next.ShowChineseSearchTokenizerStatus = true;

            pluginOptions.Enhance = next;
            this.pluginOptionsStore.SetOptions(pluginOptions);
        }

        private static StatusItem BuildChineseSearchTokenizerStatus(EnhanceOptions options)
        {
            var tokenizerName = ChineseSearch.CurrentTokenizerName ?? "unknown";
            var isSimple = string.Equals(tokenizerName, "simple", StringComparison.Ordinal);
            var isUnicode61 = tokenizerName.StartsWith("unicode61", StringComparison.Ordinal);

            if (isSimple)
            {
                return new StatusItem(
                    "生效中",
                    "当前分词器：simple（中文增强分词）",
                    ItemStatus.Succeeded);
            }

            if (isUnicode61)
            {
                if (options?.EnhanceChineseSearch == true)
                {
                    return new StatusItem(
                        "未生效",
                        "当前分词器：unicode61（默认分词器）",
                        ItemStatus.Warning);
                }

                return new StatusItem(
                    "已关闭",
                    "当前分词器：unicode61（默认分词器）",
                    ItemStatus.Succeeded);
            }

            return new StatusItem(
                "状态未知",
                $"当前分词器：{tokenizerName}",
                ItemStatus.Warning);
        }
    }
}
