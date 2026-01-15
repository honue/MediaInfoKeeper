namespace MediaInfoKeeper.Options.UIBaseClasses.Store
{
    using System;
    using Emby.Web.GenericEdit;

    internal class FileSavingEventArgs : EventArgs
    {
        public FileSavingEventArgs(EditableOptionsBase options)
        {
            this.Options = options;
        }

        public EditableOptionsBase Options { get; }

        public bool Cancel { get; set; }
    }
}
