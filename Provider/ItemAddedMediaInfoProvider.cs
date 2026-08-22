namespace MediaInfoKeeper.Provider {
    /// <summary>媒体库级入库 MediaInfo 提取开关。</summary>
    public sealed class ItemAddedMediaInfoProvider : ItemAddedProviderBase {
        public const string ProviderName = "MediaInfoKeeper 入库媒体信息";

        public override string Name => ProviderName;
    }
}
