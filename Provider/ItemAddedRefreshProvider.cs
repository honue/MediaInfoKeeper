namespace MediaInfoKeeper.Provider {
    /// <summary>
    ///     媒体库级入库刮削开关。Provider 本身不导入元数据，启用状态由 OnItemAdded 使用。
    /// </summary>
    public sealed class ItemAddedRefreshProvider : ItemAddedProviderBase {
        public const string ProviderName = "MediaInfoKeeper 入库刮削";

        public override string Name => ProviderName;
    }
}
