using System.Threading.Tasks;

namespace MediaInfoKeeper.Options.View {
    internal sealed class ItemAddedTaskDialogView :
        MainPageTaskDialogView<MainPageOptions.ItemAddedTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public ItemAddedTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ItemAddedTaskEditor ?? new MainPageOptions.ItemAddedTaskEditorOptions(),
                "入库处理") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner != null) owner.ItemAddedTaskEditor = Options;
        }
    }
}
