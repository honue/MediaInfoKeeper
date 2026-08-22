namespace MediaInfoKeeper.Provider {
    /// <summary>媒体库级入库片头扫描开关。</summary>
    public sealed class ItemAddedIntroScanProvider : ItemAddedProviderBase {
        public const string ProviderName = "MediaInfoKeeper 入库片头扫描";

        public override string Name => ProviderName;
    }
}
