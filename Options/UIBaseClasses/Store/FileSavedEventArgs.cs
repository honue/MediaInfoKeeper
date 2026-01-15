namespace MediaInfoKeeper.Options.UIBaseClasses.Store
{
    using System;
    using Emby.Web.GenericEdit;

    internal class FileSavedEventArgs : EventArgs
    {
        public FileSavedEventArgs(EditableOptionsBase options)
        {
            this.Options = options;
        }

        public EditableOptionsBase Options { get; }
    }
}
