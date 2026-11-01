using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace ClipVault
{
    /// <summary>A folder card in the list.</summary>
    public class FolderVM : INotifyPropertyChanged
    {
        public readonly Folder Folder;
        int count;
        bool dropHover;

        public event PropertyChangedEventHandler PropertyChanged;

        public FolderVM(Folder f) { Folder = f; }

        void Notify(string name) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name)); }

        public bool IsHeader { get { return false; } }
        public string Name { get { return Folder.Name; } }
        public string CountText { get { return count == 1 ? "1 item" : count + " items"; } }

        public void Refresh(int itemCount)
        {
            count = itemCount;
            Notify("Name");
            Notify("CountText");
        }

        /// <summary>True while an item is dragged over this folder (shows an accent outline).</summary>
        public bool DropHover
        {
            get { return dropHover; }
            set { if (dropHover != value) { dropHover = value; Notify("DropVis"); } }
        }

        public Visibility DropVis { get { return dropHover ? Visibility.Visible : Visibility.Collapsed; } }
    }

    /// <summary>A section title in the list ("Folders").</summary>
    public class HeaderVM
    {
        public HeaderVM(string text) { Text = text; }
        public string Text { get; private set; }
        public bool IsHeader { get { return true; } }
    }

    /// <summary>Picks the card design for each entry: clipboard item, folder, or section title.</summary>
    public class CardTemplateSelector : DataTemplateSelector
    {
        readonly DataTemplate item, folder, header;

        public CardTemplateSelector(DataTemplate item, DataTemplate folder, DataTemplate header)
        {
            this.item = item;
            this.folder = folder;
            this.header = header;
        }

        public override DataTemplate SelectTemplate(object o, DependencyObject container)
        {
            if (o is FolderVM) return folder;
            if (o is HeaderVM) return header;
            return item;
        }
    }
}
