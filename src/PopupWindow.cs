using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ClipVault
{
    /// <summary>What one card in the list shows.</summary>
    public class ItemVM : INotifyPropertyChanged
    {
        public readonly ClipItem Item;
        readonly Store store;
        BitmapSource thumb;
        bool loadingThumb, actionsOpen;

        public event PropertyChangedEventHandler PropertyChanged;

        public ItemVM(ClipItem item, Store store) { Item = item; this.store = store; }

        void Notify(params string[] names)
        {
            if (PropertyChanged == null) return;
            foreach (var n in names) PropertyChanged(this, new PropertyChangedEventArgs(n));
        }

        public void Refresh() { Notify("Tag", "TagVis", "PinVis", "PinGlyph", "PinTip", "PinBrush", "Preview", "Ago"); }

        public bool IsHeader { get { return false; } }

        /// <summary>"Just now", "2 min ago", "3 h ago", "Yesterday", "4 days ago", or a date.</summary>
        public string Ago
        {
            get
            {
                var age = DateTime.UtcNow - new DateTime(Item.Created);
                if (age.TotalMinutes < 1) return "Just now";
                if (age.TotalHours < 1) return (int)age.TotalMinutes + " min ago";
                if (age.TotalDays < 1) return (int)age.TotalHours + " h ago";
                if (age.TotalDays < 2) return "Yesterday";
                if (age.TotalDays < 7) return (int)age.TotalDays + " days ago";
                return new DateTime(Item.Created, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy");
            }
        }

        // Card pin button: outline pin when not pinned, filled accent pin when pinned.
        public string PinGlyph { get { return Item.Pinned ? "\uE840" : "\uE718"; } }
        public string PinTip { get { return Item.Pinned ? "Unpin" : "Pin"; } }
        public Brush PinBrush { get { return (Brush)Application.Current.Resources[Item.Pinned ? "FgBrush" : "Fg2Brush"]; } }

        static Visibility Vis(bool b) { return b ? Visibility.Visible : Visibility.Collapsed; }

        public Visibility TextVis { get { return Vis(Item.Kind == ItemKind.Text); } }
        public Visibility ImageVis { get { return Vis(Item.Kind == ItemKind.Image); } }
        public Visibility FilesVis { get { return Vis(Item.Kind == ItemKind.Files); } }
        public Visibility TagVis { get { return Vis(Item.Tag != null); } }
        public Visibility PinVis { get { return Vis(Item.Pinned); } }
        public string Tag { get { return Item.Tag; } }

        public bool ActionsOpen
        {
            get { return actionsOpen; }
            set { if (actionsOpen != value) { actionsOpen = value; Notify("ActionsOpen", "ActionsOffset"); } }
        }

        // Actions wrap 3 per row (5 on most cards, 6 on images with Open in Paint), so the slide is always 3 buttons wide.
        int StripWidth { get { return 118; } } // 3 buttons (34 px) per row + margins
        public GridLength ActionsWidth { get { return new GridLength(StripWidth); } }
        public Thickness StripMargin { get { return new Thickness(0, 0, -StripWidth, 0); } }
        /// <summary>How far the card slides: 0 when closed. The XAML triggers animate to this value.</summary>
        public int ActionsOffset { get { return actionsOpen ? -StripWidth : 0; } }

        public string Preview
        {
            get
            {
                string t = Item.Text ?? "";
                if (t.Length > 400) t = t.Substring(0, 400);
                return t.Replace("\t", "    ").Replace("\r\n", "\n").Trim('\n', '\r');
            }
        }


        bool AnyMissing { get { return Item.Files != null && Item.Files.All(f => !File.Exists(f) && !Directory.Exists(f)); } }

        public double ContentOpacity { get { return Item.Kind == ItemKind.Files && AnyMissing ? 0.5 : 1.0; } }

        public string FileGlyph
        {
            get { return Item.Files != null && Item.Files.Length == 1 && !Directory.Exists(Item.Files[0]) ? "\uE8A5" : "\uE8B7"; }
        }

        public string FilesTitle
        {
            get
            {
                if (Item.Files == null || Item.Files.Length == 0) return "";
                string first = Path.GetFileName(Item.Files[0].TrimEnd('\\'));
                if (string.IsNullOrEmpty(first)) first = Item.Files[0];
                return Item.Files.Length == 1 ? first : first + "  +" + (Item.Files.Length - 1) + " more";
            }
        }

        public string FilesSub
        {
            get
            {
                if (Item.Files == null || Item.Files.Length == 0) return "";
                if (AnyMissing) return "No longer exists";
                return Path.GetDirectoryName(Item.Files[0].TrimEnd('\\')) ?? Item.Files[0];
            }
        }

        // Third action depends on the type: plain text / save image / open location.
        public string ThirdGlyph { get { return Item.Kind == ItemKind.Text ? "Aa" : Item.Kind == ItemKind.Image ? "\uE74E" : "\uE838"; } }
        public FontFamily ThirdFont { get { return (FontFamily)Application.Current.Resources[Item.Kind == ItemKind.Text ? "UiFont" : "IconFont"]; } }
        public double ThirdSize { get { return Item.Kind == ItemKind.Text ? 13 : 14; } }
        public string ThirdTip { get { return Item.Kind == ItemKind.Text ? "Paste as plain text" : Item.Kind == ItemKind.Image ? "Save image as\u2026" : "Open file location"; } }

        public static double ThumbPixelsW = 640, ThumbPixelsH = 280;

        /// <summary>Small decoded thumbnail, loaded in the background on first use and dropped when the popup closes.</summary>
        public ImageSource Thumbnail
        {
            get
            {
                if (thumb != null || loadingThumb || Item.Kind != ItemKind.Image) return thumb;
                loadingThumb = true;
                string path = store.FilePath(Item, ".png");
                int w = Item.Width, h = Item.Height;
                var ui = Application.Current.Dispatcher;
                Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(path)) return;
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        if (w > 0 && h > 0)
                        {
                            if ((double)w / h > ThumbPixelsW / ThumbPixelsH) { if (w > ThumbPixelsW) bi.DecodePixelWidth = (int)ThumbPixelsW; }
                            else if (h > ThumbPixelsH) bi.DecodePixelHeight = (int)ThumbPixelsH;
                        }
                        bi.EndInit();
                        bi.Freeze();
                        ui.BeginInvoke(new Action(() => { thumb = bi; loadingThumb = false; Notify("Thumbnail"); }));
                    }
                    catch (Exception ex) { Log.Write(ex); }
                });
                return null;
            }
        }

        public void DropThumbnail() { thumb = null; loadingThumb = false; }

        public bool Matches(string query)
        {
            if (string.IsNullOrEmpty(query)) return true;
            var c = StringComparison.OrdinalIgnoreCase;
            if (Item.Tag != null && Item.Tag.IndexOf(query, c) >= 0) return true;
            switch (Item.Kind)
            {
                case ItemKind.Text: return (Item.Text ?? "").IndexOf(query, c) >= 0;
                case ItemKind.Files: return Item.Files != null && Item.Files.Any(f => f.IndexOf(query, c) >= 0);
                default: return "image picture screenshot".IndexOf(query, c) >= 0;
            }
        }
    }

    public class PopupWindow : Window
    {
        const string AllTab = "\u0001all", TextTab = "\u0001text", ImagesTab = "\u0001images", FilesTab = "\u0001files";

        readonly Store store;
        readonly ClipboardService clipboard;
        readonly ObservableCollection<object> view = new ObservableCollection<object>(); // ItemVM, FolderVM or HeaderVM
        readonly Dictionary<string, FolderVM> folderVms = new Dictionary<string, FolderVM>();
        readonly HeaderVM foldersHeader = new HeaderVM("Folders");
        string currentFolder;      // null = top level of the tab
        string nameFor;            // while the name box is open: "new" or the id of the folder being renamed
        ClipItem moveAfterCreate;  // "New folder..." from an item's menu moves that item into it
        const string DragFormat = "ClipVault.ItemId";
        readonly Dictionary<string, ItemVM> vms = new Dictionary<string, ItemVM>();
        readonly FrameworkElement root;
        readonly TextBox search, newTag;
        readonly ListBox list;
        readonly StackPanel tabs, emptyPanel, emojiHost;
        readonly TextBlock emptyText;
        readonly ScrollViewer emojiScroll, tabScroll;
        readonly RadioButton emojiTab;
        IntPtr hwnd, target;
        string currentTab = AllTab;
        bool emojiBuilt, emojiRendering, pasting;

        readonly SettingsPage settingsPage;
        readonly ContentControl settingsHost;
        bool settingsVisible, settingsOnOpen;
        readonly System.Windows.Threading.DispatcherTimer focusWatch;
        bool hadFocus; // only auto-close after the popup actually received focus

        public PopupWindow(Store store, ClipboardService clipboard)
        {
            this.store = store;
            this.clipboard = clipboard;

            Title = "ClipVault";
            Width = 360;
            Height = 440;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000;
            Top = -10000;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal); // same smooth layout Windows 11 apps use

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Popup.xaml"))
                root = (FrameworkElement)XamlReader.Load(s);
            Content = root;

            search = Find<TextBox>("Search");
            newTag = Find<TextBox>("NewTag");
            list = Find<ListBox>("List");
            tabs = Find<StackPanel>("Tabs");
            emptyPanel = Find<StackPanel>("Empty");
            emptyText = Find<TextBlock>("EmptyText");
            emojiScroll = Find<ScrollViewer>("EmojiScroll");
            emojiHost = Find<StackPanel>("EmojiHost");
            tabScroll = Find<ScrollViewer>("TabScroll");
            emojiTab = Find<RadioButton>("EmojiTab");
            list.ItemsSource = view;
            list.ItemTemplateSelector = new CardTemplateSelector(
                (DataTemplate)list.Resources["ItemCard"], (DataTemplate)list.Resources["FolderCard"], (DataTemplate)list.Resources["HeaderRow"]);
            SetupFolders();

            Find<Button>("ClearAll").Click += delegate { store.ClearUnpinned(); };
            settingsPage = new SettingsPage(store);
            settingsHost = Find<ContentControl>("SettingsHost");
            settingsHost.Content = settingsPage.Root;
            settingsPage.BackRequested += delegate { ShowSettings(false); };
            settingsPage.ScaleChanged += delegate { ApplyScale(); KeepOnScreen(); };
            Find<Button>("SettingsButton").Click += delegate { ShowSettings(true); };
            Find<Button>("DetailBack").Click += delegate { ShowPage(Page.List); };
            Find<Button>("DetailPaste").Click += delegate { if (detailItem != null) { SaveDetailEdits(); Paste(detailItem, false); } };
            Find<Button>("AddTag").Click += delegate { StartNewTag(); };
            emojiTab.Checked += delegate { SelectTab(null); };
            var clearSearch = Find<Button>("ClearSearch");
            clearSearch.Click += delegate { search.Text = ""; search.Focus(); };
            search.TextChanged += delegate { clearSearch.Visibility = search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; };
            Activated += delegate { hadFocus = true; };
            search.TextChanged += delegate { if (emojiScroll.Visibility == Visibility.Visible) FilterEmoji(); else Rebuild(); };
            newTag.KeyDown += NewTagKeyDown;
            newTag.LostKeyboardFocus += delegate { newTag.Visibility = Visibility.Collapsed; };
            tabScroll.PreviewMouseWheel += (s, e) => { tabScroll.ScrollToHorizontalOffset(tabScroll.HorizontalOffset - e.Delta / 2.0); e.Handled = true; };

            list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCardButton));
            list.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnCardClick), true);
            PreviewKeyDown += OnKey;
            PreviewTextInput += OnTextInput;
            // Our own dialogs (folder picker...) don't count. Losing focus is handled by the focus watch,
            // which waits a tick first: this event also fires for split-second focus changes while opening.
            Deactivated += delegate { if (!pasting && !IsOurs(Native.GetForegroundWindow())) ReclaimFromShell(); };

            focusWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            focusWatch.Tick += delegate { CheckFocus(); };

            store.Changed += () => Dispatcher.BeginInvoke(new Action(Rebuild));
            store.TagsChanged += () => Dispatcher.BeginInvoke(new Action(() => { BuildTabs(); if (settingsVisible) settingsPage.Refresh(); }));
            BuildTabs();
            Rebuild();
        }

        T Find<T>(string name) where T : class { return root.FindName(name) as T; }

        /// <summary>Opens the panel straight on the settings page (tray menu, --settings).</summary>
        public void OpenSettings()
        {
            if (IsVisible) { ShowSettings(true); return; }
            settingsOnOpen = true;
            ShowAt(Native.GetForegroundWindow());
        }

        // ----- pages inside the panel: the history list, Settings, or one expanded item -----

        enum Page { List, Settings, Detail }
        Page page = Page.List;
        ItemVM detailItem;

        void ShowSettings(bool on) { ShowPage(on ? Page.Settings : Page.List); }

        /// <summary>Switches what the panel shows, with a short slide (forward from the right, back from the left).</summary>
        void ShowPage(Page next)
        {
            bool changed = next != page;
            page = next;
            settingsVisible = next == Page.Settings;
            var listVis = next == Page.List ? Visibility.Visible : Visibility.Collapsed;
            var header = Find<FrameworkElement>("Header");
            var tabsRow = Find<FrameworkElement>("TabsRow");
            var body = Find<FrameworkElement>("Body");
            var detail = Find<FrameworkElement>("DetailHost");
            if (next == Page.List) UpdateChrome();
            else
            {
                header.Visibility = Visibility.Collapsed;
                tabsRow.Visibility = Visibility.Collapsed;
                Find<FrameworkElement>("FolderBar").Visibility = Visibility.Collapsed;
                Find<FrameworkElement>("NameRow").Visibility = Visibility.Collapsed;
            }
            body.Visibility = listVis;
            settingsHost.Visibility = next == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
            detail.Visibility = next == Page.Detail ? Visibility.Visible : Visibility.Collapsed;
            if (next != Page.Detail) { SaveDetailEdits(); Find<ContentControl>("DetailContent").Content = null; detailItem = null; detailEditor = null; } // free the full-size image
            if (!changed || !IsVisible) return;
            switch (next)
            {
                case Page.Settings:
                    settingsPage.Refresh();
                    SlideIn(settingsHost, 24, 0, 170);
                    settingsHost.Focus();
                    break;
                case Page.Detail:
                    SlideIn(detail, 24, 0, 170);
                    if (detailEditor != null) { detailEditor.Focus(); detailEditor.CaretIndex = 0; }
                    else Find<Button>("DetailBack").Focus();
                    break;
                default:
                    Rebuild();
                    SlideIn(currentFolder == null ? header : Find<FrameworkElement>("FolderBar"), -24, 0, 170);
                    SlideIn(tabsRow, -24, 0, 170);
                    SlideIn(body, -24, 0, 170);
                    if (lastExpanded != null && view.Contains(lastExpanded)) { list.SelectedItem = lastExpanded; list.ScrollIntoView(lastExpanded); FocusSelected(); }
                    else FocusFirst();
                    lastExpanded = null;
                    break;
            }
        }

        ItemVM lastExpanded;
        TextBox detailEditor;   // the editable text of an expanded text item (null for images/files)
        string detailOriginal;

        /// <summary>Saves edits made in the expanded view back into the item.</summary>
        void SaveDetailEdits()
        {
            if (detailEditor == null || detailItem == null) return;
            string text = detailEditor.Text;
            if (text == detailOriginal || string.IsNullOrEmpty(text)) return;
            store.UpdateText(detailItem.Item, text);
            detailOriginal = text;
        }

        /// <summary>Shows one item in full inside the panel.</summary>
        void Expand(ItemVM vm)
        {
            CloseActions(null);
            detailItem = vm;
            lastExpanded = vm;
            var it = vm.Item;
            var fg2 = (Brush)Application.Current.Resources["Fg2Brush"];
            FrameworkElement content;
            string title;
            switch (it.Kind)
            {
                case ItemKind.Text:
                    string text = store.GetText(it) ?? "";
                    title = "Text \u00B7 " + text.Length.ToString("N0") + " characters";
                    const int limit = 200000;
                    if (text.Length > limit) text = text.Substring(0, limit) + "\n\n\u2026 (showing the first " + limit.ToString("N0") + " characters)";
                    // Editable; changes are saved to the item when leaving this view or pasting.
                    bool truncated = text.Length > limit + 100;
                    var editor = new TextBox
                    {
                        Text = text, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
                        Height = double.NaN, MinHeight = 120, VerticalContentAlignment = VerticalAlignment.Top,
                        Background = Brushes.Transparent, Padding = new Thickness(2, 4, 2, 4), IsReadOnly = truncated,
                        ToolTip = truncated ? "Too long to edit here" : null
                    };
                    ToolTipService.SetInitialShowDelay(editor, 1500);
                    detailEditor = truncated ? null : editor;
                    detailOriginal = text;
                    if (!truncated) title = "Text \u00B7 edits are saved";
                    content = editor;
                    break;
                case ItemKind.Image:
                    title = "Image";
                    var img = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    string path = store.FilePath(it, ".png");
                    double maxPx = Math.Max(800, ItemVM.ThumbPixelsW * 2);
                    Task.Run(() =>
                    {
                        try
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            if (it.Width > maxPx) bi.DecodePixelWidth = (int)maxPx; // sharp at panel width, without loading huge screenshots fully
                            bi.EndInit();
                            bi.Freeze();
                            Dispatcher.BeginInvoke(new Action(() => { if (detailItem == vm) img.Source = bi; }));
                        }
                        catch (Exception ex) { Log.Write(ex); }
                    });
                    content = img;
                    break;
                default:
                    var files = it.Files ?? new string[0];
                    title = files.Length == 1 ? "1 file or folder" : files.Length + " files and folders";
                    var stack = new StackPanel();
                    foreach (var f in files)
                    {
                        bool isDir = Directory.Exists(f), exists = isDir || File.Exists(f);
                        var row = new Grid { Margin = new Thickness(0, 3, 0, 3), Opacity = exists ? 1 : 0.5 };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        row.ColumnDefinitions.Add(new ColumnDefinition());
                        row.Children.Add(new TextBlock { Text = isDir ? "\uE8B7" : "\uE8A5", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 16, Margin = new Thickness(0, 2, 10, 0) });
                        var name = new StackPanel();
                        name.Children.Add(new TextBlock { Text = Path.GetFileName(f.TrimEnd('\\')), TextWrapping = TextWrapping.Wrap });
                        name.Children.Add(new TextBlock { Text = exists ? Path.GetDirectoryName(f.TrimEnd('\\')) : "No longer exists", FontSize = 12, Foreground = fg2, TextWrapping = TextWrapping.Wrap });
                        Grid.SetColumn(name, 1);
                        row.Children.Add(name);
                        stack.Children.Add(row);
                    }
                    content = stack;
                    break;
            }
            Find<TextBlock>("DetailTitle").Text = title;
            Find<ContentControl>("DetailContent").Content = content;
            Find<ScrollViewer>("DetailScroll").ScrollToTop();
            ShowPage(Page.Detail);
        }
        /// <summary>After a size change, nudge the panel back inside the screen if it grew past an edge.</summary>
        void KeepOnScreen()
        {
            UpdateLayout();
            Native.RECT r;
            Native.GetWindowRect(hwnd, out r);
            var work = Native.WorkAreaAt(new Native.POINT { X = r.Left, Y = r.Top });
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            int x = Math.Max(work.Left, Math.Min(r.Left, work.Right - w));
            int y = Math.Max(work.Top, Math.Min(r.Top, work.Bottom - h));
            if (x != r.Left || y != r.Top) Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        double uiScale = 1;

        /// <summary>The Size setting: zooms text, icons and cards together, and grows the popup to match.</summary>
        void ApplyScale()
        {
            double s = store.Settings.UiScale;
            if (Math.Abs(s - uiScale) < 0.001 && root.LayoutTransform is ScaleTransform) return;
            uiScale = s;
            root.LayoutTransform = new ScaleTransform(s, s);
            Width = 360 * s;
            Height = 440 * s;
            emojiBuilt = false; // re-render emoji at the new size next time the tab opens
        }

        /// <summary>Short fade + slide into place. Kept under 200 ms so it never feels slow.</summary>
        static void SlideIn(FrameworkElement el, double fromX, double fromY, int ms)
        {
            var tt = el.RenderTransform as TranslateTransform;
            if (tt == null) { tt = new TranslateTransform(); el.RenderTransform = tt; }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(ms);
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, d) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, d) { EasingFunction = ease });
        }

        int TabOrder(string key)
        {
            if (key == null) return 1000; // emoji tab sits on the far right
            var keys = new List<string> { AllTab, TextTab, ImagesTab, FilesTab };
            keys.AddRange(store.Index.Tags);
            int i = keys.IndexOf(key);
            return i < 0 ? 0 : i;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            hwnd = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(hwnd);
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        // ---------- showing / hiding ----------

        bool opening;

        public async void ShowAt(IntPtr foreground)
        {
            if (opening) return;
            if (IsVisible) { HidePopup(); return; }
            target = foreground;
            shownAt = DateTime.UtcNow; // from here on, Windows pop-ups are reclaimed instead of closing us
            lostFocusTicks = 0;
            opening = true;
            Anchor where;
            try
            {
                // Start and Windows Search sit on a layer above every normal window, so the panel would open
                // behind them. Close them first; the app that was in front before becomes the paste target.
                if (IsShellFlyout(foreground))
                {
                    Native.TapKey(0x1B); // Esc
                    await Task.Delay(150);
                    foreground = Native.GetForegroundWindow();
                    if (IsShellFlyout(foreground)) foreground = IntPtr.Zero;
                    target = foreground;
                }
                // Find the text cursor before showing (the lookup may take up to ~250 ms in some apps).
                where = await CaretLocator.Find(foreground);
            }
            finally { opening = false; }
            var anchor = where.Point;
            bool fromCaret = where.FromText;
            int caretH = where.LineHeight;

            Theme.Apply();
            ApplyScale();
            new WindowInteropHelper(this).EnsureHandle();
            Native.ApplyWindowEffects(hwnd, Theme.IsDark, 3 /* acrylic, like the Windows clipboard panel */);

            // Reset to a clean state.
            currentFolder = null;
            nameFor = null;
            moveAfterCreate = null;
            search.Text = "";
            newTag.Visibility = Visibility.Collapsed;
            if (currentTab == null) { currentTab = AllTab; }
            BuildTabs();
            ShowEmoji(false);
            ShowSettings(false);
            Rebuild();

            var work = Native.WorkAreaAt(anchor);

            // Move onto the target monitor first so WPF can rescale for that monitor's DPI, then measure.
            Native.SetWindowPos(hwnd, IntPtr.Zero, anchor.X, anchor.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Show();
            Native.RECT r;
            Native.GetWindowRect(hwnd, out r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            double scale = w / Width;
            ItemVM.ThumbPixelsW = Math.Round(300 * scale * uiScale);
            ItemVM.ThumbPixelsH = Math.Round(140 * scale * uiScale);
            int gap = (int)(8 * scale);

            int x = anchor.X, y = fromCaret ? anchor.Y + caretH + gap : anchor.Y + gap;
            if (y + h > work.Bottom) y = (fromCaret ? anchor.Y - gap : anchor.Y - gap) - h;
            x = Math.Max(work.Left + gap, Math.Min(x, work.Right - w - gap));
            y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - h - gap));
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);

            Native.TapDummyKey();
            Native.ForceForeground(hwnd);
            Activate();
            FocusFirst();
            // Opens upward from below the caret (or downward when it had to go above it).
            SlideIn((FrameworkElement)((Border)root).Child, 0, y > anchor.Y ? 14 : -14, 190);
            if (settingsOnOpen) { settingsOnOpen = false; ShowSettings(true); }
            foregroundAtShow = Native.GetForegroundWindow();
            focusWatch.Start();
            StartOutsideClickWatch();
        }

        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;

        // Start, Search, and TextInputHost (Windows' own Win+V clipboard / emoji panel).
        static readonly string[] shellFlyouts = { "StartMenuExperienceHost", "SearchHost", "SearchApp", "ShellExperienceHost", "TextInputHost" };
        DateTime shownAt;

        /// <summary>
        /// Right after opening, a Windows pop-up (its own clipboard panel, Start, Search) can grab focus, e.g. when
        /// Windows also reacted to Win+V. Close that pop-up and take focus back instead of closing ClipVault.
        /// </summary>
        bool ReclaimFromShell()
        {
            // Only right after opening. 1.5 s was not enough: Windows starts its clipboard panel slowly
            // the first time, and by the time it appeared this check had already timed out.
            if (!IsVisible || shownAt == DateTime.MinValue || (DateTime.UtcNow - shownAt).TotalMilliseconds > 3000) return false;
            IntPtr fg = Native.GetForegroundWindow();
            if (!IsShellFlyout(fg)) return false;
            Log.Write("A Windows pop-up (" + ForegroundName() + ") took focus while opening; closed it and kept ClipVault open.");
            if (Native.GetForegroundWindow() == fg) Native.TapKey(0x1B); // Esc closes it (only if it still has focus, or it would close us)
            Dispatcher.BeginInvoke(new Action(() => { Native.TapDummyKey(); Native.ForceForeground(hwnd); Activate(); }),
                System.Windows.Threading.DispatcherPriority.Background);
            return true;
        }

        /// <summary>Name of the process owning the foreground window, for the log.</summary>
        static string ForegroundName()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return "none";
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            try
            {
                using (var p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return "pid " + pid; }
        }

        /// <summary>True when the window belongs to the Start menu or Windows Search.</summary>
        static bool IsShellFlyout(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    return shellFlyouts.Any(n => string.Equals(n, p.ProcessName, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }
        IntPtr foregroundAtShow;

        static bool IsOurs(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid == myPid;
        }

        /// <summary>
        /// Backup for the Deactivated event: close when the front window changes to another app
        /// (Alt+Tab, taskbar...). Works even when Windows never gave the popup focus.
        /// Our own menus and dialogs belong to this process, so they don't count.
        /// </summary>
        int lostFocusTicks;

        void CheckFocus()
        {
            if (!IsVisible) { focusWatch.Stop(); return; }
            if (pasting) return;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOurs(fg)) { lostFocusTicks = 0; return; }
            if (ReclaimFromShell()) { lostFocusTicks = 0; return; }
            if (fg == foregroundAtShow && !hadFocus) { lostFocusTicks = 0; return; }
            // One tick (150 ms) of another window is not enough: while the panel is opening, focus passes
            // through other windows (the app that had it, Windows' own pop-ups) before it settles here.
            if (++lostFocusTicks < 2) return;
            HidePopup("focus moved to " + ForegroundName());
        }

        // ----- click outside = close (a mouse hook that only exists while the popup is open) -----

        Native.LowLevelKeyboardProc mouseProc; // same signature as the mouse hook; kept so the GC can't collect it
        IntPtr mouseHook;

        void StartOutsideClickWatch()
        {
            if (mouseHook != IntPtr.Zero) return;
            if (mouseProc == null) mouseProc = MouseCallback;
            mouseHook = Native.SetWindowsHookEx(14 /* WH_MOUSE_LL */, mouseProc, Native.GetModuleHandle(null), 0);
        }

        void StopOutsideClickWatch()
        {
            if (mouseHook == IntPtr.Zero) return;
            Native.UnhookWindowsHookEx(mouseHook);
            mouseHook = IntPtr.Zero;
        }

        IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                // left, right, middle or X button pressed
                if (msg == 0x201 || msg == 0x204 || msg == 0x207 || msg == 0x20B)
                {
                    // Ignore a click still in flight when the panel opens (tray icon double-click),
                    // or it would close the panel it just opened.
                    bool settling = shownAt != DateTime.MinValue && (DateTime.UtcNow - shownAt).TotalMilliseconds < 350;
                    var info = (Native.MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (!settling && !IsOurs(Native.WindowFromPoint(info.pt)))
                        Dispatcher.BeginInvoke(new Action(() => { if (!pasting) HidePopup("click outside"); }));
                }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        public void HidePopup() { HidePopup(null); }

        /// <summary>Closes the panel. <paramref name="why"/> is set for closes the user didn't ask for, which are logged.</summary>
        void HidePopup(string why)
        {
            if (!IsVisible) return;
            long ms = shownAt == DateTime.MinValue ? -1 : (long)(DateTime.UtcNow - shownAt).TotalMilliseconds;
            if (why != null && ms >= 0 && ms < 2000) Log.Write("Closed " + ms + " ms after opening (" + why + "; had focus: " + hadFocus + ")");
            shownAt = DateTime.MinValue;
            lostFocusTicks = 0;
            SaveDetailEdits(); // closing the panel while editing keeps the edits
            focusWatch.Stop();
            StopOutsideClickWatch();
            hadFocus = false;
            foreach (var vm in vms.Values) { vm.ActionsOpen = false; vm.DropThumbnail(); }
            Hide();
            view.Clear(); // releases cards and thumbnails while hidden; rebuilt on next show
            emojiScroll.ScrollToTop();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += delegate { timer.Stop(); if (!IsVisible) Native.TrimMemory(); };
            timer.Start();
        }

        void FocusFirst()
        {
            int first = view.IndexOf(view.FirstOrDefault(v => !(v is HeaderVM)));
            if (first < 0) { if (currentFolder == null) search.Focus(); else list.Focus(); return; }
            list.SelectedIndex = first;
            list.ScrollIntoView(view[first]);
            list.UpdateLayout();
            var item = list.ItemContainerGenerator.ContainerFromIndex(first) as ListBoxItem;
            if (item != null) item.Focus(); else list.Focus();
        }

        // ---------- tabs ----------

        void BuildTabs()
        {
            tabs.Children.Clear();
            AddTab("All", AllTab, "\uE77F");       // clipboard
            AddTab("Text", TextTab, "\uE8D2");     // font "A"
            AddTab("Images", ImagesTab, "\uEB9F"); // photo
            AddTab("Files", FilesTab, "\uE8B7");   // folder
            foreach (var t in store.Index.Tags) AddTab(t, t, null); // your tags: their icon if they have one, else their name
            if (currentTab != null && currentTab != AllTab && currentTab != TextTab && currentTab != ImagesTab && currentTab != FilesTab
                && !store.Index.Tags.Contains(currentTab)) currentTab = AllTab;
        }

        void AddTab(string label, string key, string glyph)
        {
            bool isTag = glyph == null;
            object content = isTag ? (object)label
                : new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 16, Margin = new Thickness(2, 0, 2, 0) };
            if (isTag)
            {
                var icon = TagIcons.Make(store.TagIcon(key), 16);
                if (icon != null) { icon.Margin = new Thickness(2, 0, 2, 0); content = icon; }
            }
            var rb = new RadioButton { Content = content, ToolTip = isTag ? label + " (right-click to change icon)" : label, GroupName = "tabs", Style = (Style)Application.Current.Resources["Tab"], IsChecked = key == currentTab };
            if (isTag) rb.MouseRightButtonUp += (s, e) => { TagIcons.ShowPicker(rb, store, key); e.Handled = true; };
            rb.Checked += delegate { SelectTab(key); };
            tabs.Children.Add(rb);
        }

        void SelectTab(string key)
        {
            if (IsVisible && key != currentTab)
                SlideIn(Find<FrameworkElement>("Body"), TabOrder(key) > TabOrder(currentTab) ? 18 : -18, 0, 170);
            currentTab = key;
            currentFolder = null;
            ShowEmoji(key == null);
            if (key != null) { Rebuild(); FocusFirst(); }
        }

        void StartNewTag()
        {
            newTag.Text = "";
            newTag.Visibility = Visibility.Visible;
            newTag.Focus();
        }

        void NewTagKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string name = newTag.Text.Trim();
                newTag.Visibility = Visibility.Collapsed;
                if (store.AddTag(name)) { currentTab = name; BuildTabs(); SelectTab(name); }
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                newTag.Visibility = Visibility.Collapsed;
                FocusFirst();
                e.Handled = true;
            }
        }

        // ---------- folders ----------

        TextBox nameBox;
        bool suppressClick;        // the mouse-up that ends a drag must not paste
        ItemVM dragCandidate;
        Point dragStart;
        FolderVM hoverFolder;

        void SetupFolders()
        {
            nameBox = Find<TextBox>("NameBox");
            Find<Button>("NewSubFolderButton").Click += delegate { moveAfterCreate = null; StartName("new", ""); };
            Find<Button>("FolderBack").Click += delegate { GoUp(); };
            nameBox.KeyDown += NameBoxKeyDown;
            nameBox.LostKeyboardFocus += delegate { if (nameFor != null) EndName(); };

            // Drag a card onto a folder (or a breadcrumb) to move it there.
            list.AllowDrop = true;
            list.PreviewMouseLeftButtonDown += (s, e) =>
            {
                suppressClick = false;
                var src = e.OriginalSource as DependencyObject;
                dragCandidate = InsideButton(src) ? null : VmOf(e.OriginalSource);
                dragStart = e.GetPosition(list);
            };
            list.PreviewMouseMove += OnDragMove;
            list.DragOver += (s, e) =>
            {
                var f = FolderUnder(e.OriginalSource);
                SetHover(f);
                e.Effects = f != null && e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
                AutoScroll(e.GetPosition(list).Y);
                e.Handled = true;
            };
            list.DragLeave += delegate { SetHover(null); };
            list.Drop += (s, e) =>
            {
                var f = FolderUnder(e.OriginalSource);
                SetHover(null);
                if (f != null) MoveDragged(e.Data, f.Folder.Id);
                e.Handled = true;
            };
        }

        void OnDragMove(object sender, MouseEventArgs e)
        {
            if (dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(list);
            if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var vm = dragCandidate;
            dragCandidate = null;
            suppressClick = true;
            CloseActions(null);
            var card = ContainerOf(vm);
            if (card != null) card.Opacity = 0.5;
            try { DragDrop.DoDragDrop(list, new DataObject(DragFormat, vm.Item.Id), DragDropEffects.Move); }
            catch (Exception ex) { Log.Write(ex); }
            finally
            {
                if (card != null) card.Opacity = 1;
                SetHover(null);
            }
        }

        /// <summary>Scrolls while dragging near the top or bottom edge, so far-away folders can be reached.</summary>
        void AutoScroll(double y)
        {
            var sv = FindChild<ScrollViewer>(list);
            if (sv == null) return;
            if (y < 30) sv.ScrollToVerticalOffset(sv.VerticalOffset - 12);
            else if (y > list.ActualHeight - 30) sv.ScrollToVerticalOffset(sv.VerticalOffset + 12);
        }

        static T FindChild<T>(DependencyObject d) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                var hit = c as T ?? FindChild<T>(c);
                if (hit != null) return hit;
            }
            return null;
        }

        void SetHover(FolderVM f)
        {
            if (hoverFolder == f) return;
            if (hoverFolder != null) hoverFolder.DropHover = false;
            hoverFolder = f;
            if (f != null) f.DropHover = true;
        }

        void MoveDragged(IDataObject data, string folderId)
        {
            var id = data.GetData(DragFormat) as string;
            var item = store.Index.Items.FirstOrDefault(i => i.Id == id);
            if (item != null) store.MoveToFolder(item, folderId);
        }

        static FolderVM FolderUnder(object source)
        {
            var fe = source as FrameworkElement;
            if (fe == null)
            {
                var ce = source as FrameworkContentElement;
                return ce == null ? null : ce.DataContext as FolderVM;
            }
            return fe.DataContext as FolderVM;
        }

        /// <summary>True when a folder belongs to the tab being shown (folders are bound to the tab they were made in).</summary>
        bool InThisTab(Folder f)
        {
            string tab = store.TabOf(f);
            return tab == null ? currentTab == AllTab : tab == currentTab;
        }

        /// <summary>Items in a folder (for the current tab) plus its sub-folders.</summary>
        int CountIn(string folderId)
        {
            return store.Index.Items.Count(i => i.FolderId == folderId && InTab(i)) + store.Index.Folders.Count(f => f.ParentId == folderId);
        }

        void OpenFolder(string id) { NavigateFolder(id, true); }

        void GoUp()
        {
            var f = store.FindFolder(currentFolder);
            NavigateFolder(f == null ? null : f.ParentId, false);
        }

        void NavigateFolder(string id, bool forward)
        {
            CloseActions(null);
            if (nameFor != null) EndName();
            currentFolder = id;
            if (id != null && search.Text.Length > 0) search.Text = ""; // (rebuilds)
            Rebuild();
            UpdateChrome(); // inside a folder the top row becomes back + breadcrumb
            if (IsVisible)
            {
                double dx = forward ? 24 : -24;
                SlideIn(Find<FrameworkElement>("Body"), dx, 0, 170);
                SlideIn(currentFolder == null ? Find<FrameworkElement>("Header") : Find<FrameworkElement>("FolderBar"), dx, 0, 170);
            }
            FocusFirst();
        }

        /// <summary>Shows the search header + tabs at the top level, or the back/breadcrumb bar inside a folder.</summary>
        void UpdateChrome()
        {
            if (page != Page.List) return;
            bool inFolder = currentFolder != null;
            bool naming = nameFor != null;
            Find<FrameworkElement>("Header").Visibility = inFolder ? Visibility.Collapsed : Visibility.Visible;
            Find<FrameworkElement>("FolderBar").Visibility = inFolder ? Visibility.Visible : Visibility.Collapsed;
            Find<FrameworkElement>("TabsRow").Visibility = inFolder || naming ? Visibility.Collapsed : Visibility.Visible;
            Find<FrameworkElement>("NameRow").Visibility = naming ? Visibility.Visible : Visibility.Collapsed;
            if (inFolder) BuildCrumbs();
        }

        string RootLabel()
        {
            if (currentTab == TextTab) return "Text";
            if (currentTab == ImagesTab) return "Images";
            if (currentTab == FilesTab) return "Files";
            if (currentTab == AllTab || currentTab == null) return "Clipboard";
            return currentTab;
        }

        /// <summary>Clipboard \u203A Work \u203A API. Each part opens that level, and items can be dropped on it.</summary>
        void BuildCrumbs()
        {
            var crumbs = Find<StackPanel>("Crumbs");
            crumbs.Children.Clear();
            var chain = new List<Folder>();
            for (var f = store.FindFolder(currentFolder); f != null && chain.Count < 20; f = store.FindFolder(f.ParentId)) chain.Insert(0, f);
            AddCrumb(crumbs, RootLabel(), null, chain.Count == 0);
            for (int i = 0; i < chain.Count; i++)
            {
                crumbs.Children.Add(new TextBlock
                {
                    Text = "\uE76C", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 9,
                    Foreground = (Brush)Application.Current.Resources["Fg3Brush"], VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 2, 0)
                });
                AddCrumb(crumbs, chain[i].Name, chain[i].Id, i == chain.Count - 1);
            }
            var scroll = Find<ScrollViewer>("CrumbScroll");
            Dispatcher.BeginInvoke(new Action(() => scroll.ScrollToRightEnd()), System.Windows.Threading.DispatcherPriority.Background);
        }

        void AddCrumb(StackPanel crumbs, string text, string folderId, bool current)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)Application.Current.Resources["Crumb"],
                FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)Application.Current.Resources[current ? "FgBrush" : "Fg2Brush"],
                AllowDrop = true
            };
            string id = folderId;
            if (!current) b.Click += delegate { NavigateFolder(id, false); };
            b.DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            b.Drop += (s, e) => { MoveDragged(e.Data, id); e.Handled = true; };
            crumbs.Children.Add(b);
        }

        void ShowFolderMenu(Button anchor, FolderVM fvm)
        {
            var f = fvm.Folder;
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            var open = new MenuItem { Header = "Open", Tag = "\uE8B7" };
            open.Click += delegate { OpenFolder(f.Id); };
            var rename = new MenuItem { Header = "Rename", Tag = "\uE8AC" };
            rename.Click += delegate { StartName(f.Id, f.Name); };
            int n = store.ItemsInTree(f);
            var del = new MenuItem { Header = n == 0 ? "Delete folder" : "Delete folder and its " + (n == 1 ? "1 item" : n + " items"), Tag = "\uE74D" };
            del.Click += delegate { store.DeleteFolder(f); };
            menu.Items.Add(open);
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            menu.Items.Add(del);
            menu.IsOpen = true;
        }

        /// <summary>Opens the name box: "new" creates a folder here, a folder id renames that folder.</summary>
        void StartName(string mode, string initial)
        {
            nameFor = mode;
            nameBox.Tag = mode == "new" ? "New folder name (Enter to create, Esc to cancel)" : "Rename folder (Enter to save, Esc to cancel)";
            nameBox.Text = initial;
            UpdateChrome();
            nameBox.Focus();
            nameBox.SelectAll();
        }

        void EndName()
        {
            nameFor = null;
            UpdateChrome();
        }

        void NameBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string mode = nameFor, name = nameBox.Text.Trim();
                var move = moveAfterCreate;
                moveAfterCreate = null;
                EndName();
                if (name.Length > 0)
                {
                    if (mode == "new")
                    {
                        var f = store.AddFolder(name, currentFolder, currentTab == AllTab ? null : currentTab);
                        if (f != null && move != null) store.MoveToFolder(move, f.Id);
                    }
                    else
                    {
                        var f = store.FindFolder(mode);
                        if (f != null) store.RenameFolder(f, name);
                    }
                }
                FocusFirst();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                moveAfterCreate = null;
                EndName();
                FocusFirst();
                e.Handled = true;
            }
        }
        // ---------- list ----------

        void Rebuild()
        {
            // Sync view models with the store.
            var live = new HashSet<string>(store.Index.Items.Select(i => i.Id));
            foreach (var dead in vms.Keys.Where(k => !live.Contains(k)).ToList()) vms.Remove(dead);
            foreach (var it in store.Index.Items) if (!vms.ContainsKey(it.Id)) vms[it.Id] = new ItemVM(it, store);

            if (currentFolder != null && store.FindFolder(currentFolder) == null) { currentFolder = null; UpdateChrome(); } // folder was deleted

            // Search (top level only) looks everywhere, including inside folders; otherwise show this level.
            string q = currentFolder == null ? search.Text.Trim() : "";
            bool searching = q.Length > 0;
            var wanted = new List<object>(store.Index.Items
                .Where(i => InTab(i) && (searching || i.FolderId == currentFolder))
                .Where(i => searching || !IsBuiltInTab(currentTab) || i.Tag == null) // tagged items live only in their tag's tab
                .OrderBy(i => i.Pinned)              // history first, pinned items after it
                .ThenByDescending(i => i.Created)
                .Select(i => vms[i.Id])
                .Where(vm => vm.Matches(q)));

            // Folders always come last, under a "Folders" title.
            var liveFolders = new HashSet<string>(store.Index.Folders.Select(f => f.Id));
            foreach (var dead in folderVms.Keys.Where(k => !liveFolders.Contains(k)).ToList()) folderVms.Remove(dead);
            foreach (var f in store.Index.Folders) if (!folderVms.ContainsKey(f.Id)) folderVms[f.Id] = new FolderVM(f);
            foreach (var fvm in folderVms.Values) fvm.Refresh(CountIn(fvm.Folder.Id));
            if (!searching)
            {
                var subs = store.Index.Folders.Where(f => f.ParentId == currentFolder && (currentFolder != null || InThisTab(f)))
                    .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Select(f => (object)folderVms[f.Id]).ToList();
                wanted.Add(foldersHeader);   // always shown: it carries the "New folder" button
                wanted.AddRange(subs);
            }

            object selected = list.SelectedItem;
            if (!wanted.SequenceEqual(view))
            {
                // Update the list in place (remove / move / insert) instead of clearing it,
                // so cards that stay don't get redrawn and nothing jumps.
                var keep = new HashSet<object>(wanted);
                for (int i = view.Count - 1; i >= 0; i--) if (!keep.Contains(view[i])) view.RemoveAt(i);
                for (int i = 0; i < wanted.Count; i++)
                {
                    if (i < view.Count && view[i] == wanted[i]) continue;
                    int at = view.IndexOf(wanted[i]);
                    if (at >= 0) view.Move(at, i); else view.Insert(i, wanted[i]);
                }
                if (selected != null && view.Contains(selected)) list.SelectedItem = selected;
            }
            foreach (var vm in wanted.OfType<ItemVM>()) vm.Refresh();

            bool emoji = emojiScroll.Visibility == Visibility.Visible;
            emptyPanel.Visibility = !emoji && !view.Any(v => !(v is HeaderVM)) ? Visibility.Visible : Visibility.Collapsed;
            Find<TextBlock>("EmptyIcon").Text = currentFolder != null ? "\uE8B7" : "\uE77F";
            emptyText.Text = currentFolder != null ? "This folder is empty.\nDrag items here, or use an item's Tag menu \u203A Move to folder."
                : q.Length > 0 ? "No matches for \u201C" + q + "\u201D."
                : currentTab == AllTab ? "Nothing here yet.\nCopy something with Ctrl+C and it will show up here."
                : store.Index.Tags.Contains(currentTab) ? "Nothing tagged " + currentTab + " yet.\nUse the pin button on an item and choose \u201C" + currentTab + "\u201D."
                : "Nothing here yet.";
        }

        /// <summary>All, Text, Images and Files (as opposed to a tag's tab).</summary>
        static bool IsBuiltInTab(string tab) { return tab == AllTab || tab == TextTab || tab == ImagesTab || tab == FilesTab; }

        bool InTab(ClipItem i)
        {
            if (currentTab == AllTab || currentTab == null) return true;
            if (currentTab == TextTab) return i.Kind == ItemKind.Text;
            if (currentTab == ImagesTab) return i.Kind == ItemKind.Image;
            if (currentTab == FilesTab) return i.Kind == ItemKind.Files;
            return i.Tag == currentTab;
        }

        static ItemVM VmOf(object source)
        {
            var fe = source as FrameworkElement;
            return fe == null ? null : fe.DataContext as ItemVM;
        }

        static bool InsideButton(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem))
            {
                if (d is ButtonBase) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        void CloseActions(ItemVM except)
        {
            foreach (var vm in view.OfType<ItemVM>()) if (vm != except) vm.ActionsOpen = false;
        }

        void OnCardClick(object sender, MouseButtonEventArgs e)
        {
            if (suppressClick) { suppressClick = false; return; }
            var src = e.OriginalSource as DependencyObject;
            if (InsideButton(src)) return;
            var folder = FolderUnder(e.OriginalSource);
            if (folder != null) { OpenFolder(folder.Folder.Id); return; }
            var vm = VmOf(e.OriginalSource);
            if (vm == null) return;
            if (vm.ActionsOpen || view.OfType<ItemVM>().Any(v => v.ActionsOpen)) { CloseActions(null); return; }
            Paste(vm, false);
        }

        void OnCardButton(object sender, RoutedEventArgs e)
        {
            var btn = e.OriginalSource as Button;
            if (btn == null) return;
            if (btn.DataContext is HeaderVM) { e.Handled = true; if (btn.Tag as string == "newfolder") { moveAfterCreate = null; StartName("new", ""); } return; }
            var fvmBtn = btn.DataContext as FolderVM;
            if (fvmBtn != null) { e.Handled = true; if (btn.Tag as string == "foldermore") ShowFolderMenu(btn, fvmBtn); return; }
            var vm = btn.DataContext as ItemVM;
            if (vm == null) return;
            e.Handled = true;
            switch (btn.Tag as string)
            {
                case "more":
                    CloseActions(vm);
                    vm.ActionsOpen = !vm.ActionsOpen;
                    break;
                case "claude": AskClaude(vm); break;
                case "third": Third(vm); break;
                case "togglepin":
                    // One click pins, the next click unpins (unpinning also removes the tag).
                    // The card shrinks away, then grows in at its new place (pinned items live at the bottom).
                    var item = vm.Item;
                    Collapse(vm, () => { store.SetPin(item, !item.Pinned, null); AppearAfterMove(vm); });
                    break;
                case "tagmenu": ShowTagMenu(btn, vm); break;
                case "paint": OpenInPaint(vm); break;
                case "expand": Expand(vm); break;
                case "delete":
                    var gone = vm.Item;
                    Collapse(vm, () => store.Delete(gone));
                    break;
            }
        }

        // ----- quick card animations (about 0.15 s) -----

        static readonly Duration CardAnim = new Duration(TimeSpan.FromMilliseconds(150));

        ListBoxItem ContainerOf(ItemVM vm) { return list.ItemContainerGenerator.ContainerFromItem(vm) as ListBoxItem; }

        static void ResetCard(ListBoxItem c)
        {
            c.BeginAnimation(UIElement.OpacityProperty, null);
            c.Opacity = 1;
            c.LayoutTransform = Transform.Identity;
        }

        /// <summary>Shrinks and fades a card out, then runs the action.</summary>
        void Collapse(ItemVM vm, Action done)
        {
            var c = ContainerOf(vm);
            if (c == null) { done(); return; }
            var scale = new ScaleTransform(1, 1);
            c.LayoutTransform = scale;
            var shrink = new DoubleAnimation(1, 0, CardAnim) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            shrink.Completed += delegate { done(); };
            c.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, CardAnim));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        /// <summary>After the list has re-sorted, grows the moved card in at its new position.</summary>
        void AppearAfterMove(ItemVM vm)
        {
            // Runs after the store's change has rebuilt the list (that happens at normal priority).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                list.UpdateLayout();
                foreach (var other in view.OfType<ItemVM>())
                {
                    var oc = ContainerOf(other);
                    if (oc != null && other != vm) ResetCard(oc); // a reused card must never stay collapsed
                }
                var c = ContainerOf(vm);
                if (c == null) return; // moved off-screen: nothing to animate
                var scale = new ScaleTransform(1, 0);
                c.LayoutTransform = scale;
                c.Opacity = 0;
                var grow = new DoubleAnimation(0, 1, CardAnim) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                grow.Completed += delegate { ResetCard(c); };
                c.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, CardAnim));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Opens a copy of the image in Paint, so edits never change the stored history item.</summary>
        void OpenInPaint(ItemVM vm)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "ClipVault");
                Directory.CreateDirectory(dir);
                string copy = Path.Combine(dir, "Clipboard " + new DateTime(vm.Item.Created).ToLocalTime().ToString("yyyy-MM-dd HHmmss") + ".png");
                File.Copy(store.FilePath(vm.Item, ".png"), copy, true);
                HidePopup();
                using (Process.Start(new ProcessStartInfo("mspaint.exe", Native.QuoteArg(copy)) { UseShellExecute = true })) { }
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                MessageBox.Show("Couldn't open Paint: " + ex.Message, "ClipVault", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void ShowTagMenu(Button anchor, ItemVM vm)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            var it = vm.Item;
            if (store.Index.Tags.Count == 0)
                menu.Items.Add(new MenuItem { Header = "No tags yet. Use + next to the tabs", Tag = "\uE946", IsEnabled = false });
            foreach (var t in store.Index.Tags)
            {
                string tag = t;
                var mi = new MenuItem { Header = "Pin to " + tag, Tag = "\uE8EC", IsChecked = it.Tag == tag };
                mi.Click += delegate { store.SetPin(it, true, tag); vm.ActionsOpen = false; };
                menu.Items.Add(mi);
            }
            if (it.Tag != null)
            {
                menu.Items.Add(new Separator());
                var rm = new MenuItem { Header = "Remove tag (stays pinned)", Tag = "\uE711" };
                rm.Click += delegate { store.SetPin(it, true, null); vm.ActionsOpen = false; };
                menu.Items.Add(rm);
            }

            // Folders
            menu.Items.Add(new Separator());
            foreach (var f in store.Index.Folders.Where(InThisTab).OrderBy(x => store.FolderPath(x), StringComparer.CurrentCultureIgnoreCase))
            {
                string id = f.Id;
                var mf = new MenuItem { Header = "Move to " + store.FolderPath(f), Tag = "\uE8B7", IsChecked = it.FolderId == id };
                mf.Click += delegate { vm.ActionsOpen = false; store.MoveToFolder(it, id); };
                menu.Items.Add(mf);
            }
            var nf = new MenuItem { Header = "New folder\u2026", Tag = "\uE8F4" };
            nf.Click += delegate { vm.ActionsOpen = false; moveAfterCreate = it; StartName("new", ""); };
            menu.Items.Add(nf);
            if (it.FolderId != null)
            {
                var rf = new MenuItem { Header = "Remove from folder (stays pinned)", Tag = "\uE711" };
                rf.Click += delegate { vm.ActionsOpen = false; store.MoveToFolder(it, null); };
                menu.Items.Add(rf);
            }
            menu.IsOpen = true;
        }

        void Third(ItemVM vm)
        {
            switch (vm.Item.Kind)
            {
                case ItemKind.Text:
                    Paste(vm, true);
                    break;
                case ItemKind.Image:
                    pasting = true; // keep the popup open while the dialog is up
                    try
                    {
                        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "Clipboard " + new DateTime(vm.Item.Created).ToLocalTime().ToString("yyyy-MM-dd HHmmss"), DefaultExt = ".png", Filter = "PNG image|*.png" };
                        if (dlg.ShowDialog(this) == true) File.Copy(store.FilePath(vm.Item, ".png"), dlg.FileName, true);
                    }
                    catch (Exception ex) { Log.Write(ex); }
                    finally { pasting = false; }
                    Activate();
                    vm.ActionsOpen = false;
                    break;
                case ItemKind.Files:
                    var f = vm.Item.Files.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
                    HidePopup();
                    if (f != null) using (Process.Start("explorer.exe", "/select," + Native.QuoteArg(f))) { }
                    break;
            }
        }

        void AskClaude(ItemVM vm)
        {
            var it = vm.Item;
            string prompt = it.Kind == ItemKind.Text ? store.GetText(it)
                : it.Kind == ItemKind.Image ? store.FilePath(it, ".png")
                : string.Join("\n", it.Files);
            string error = null;
            try { error = Claude.Send(prompt, store.Settings.ClaudeFolder); }
            catch (Exception ex) { Log.Write(ex); error = ex.Message; }
            HidePopup();
            if (error != null) MessageBox.Show(error, "ClipVault", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------- pasting ----------

        async void Paste(ItemVM vm, bool plain)
        {
            var sw = Stopwatch.StartNew();
            bool ok = await clipboard.PutAsync(vm.Item, plain);
            long putMs = sw.ElapsedMilliseconds;
            if (!ok) { Log.Write("Paste: couldn't put the item on the clipboard (" + vm.Item.Kind + ")"); return; }
            await ReturnAndPaste(null, vm.Item.Kind + ", clipboard write " + putMs + " ms");
            // Record slow pastes, so a freeze can be traced to the step that caused it.
            if (sw.ElapsedMilliseconds > 500) Log.Write("Slow paste (" + vm.Item.Kind + "): clipboard " + putMs + " ms, total " + sw.ElapsedMilliseconds + " ms");
        }

        async Task ReturnAndPaste(string typeText, string logInfo)
        {
            IntPtr t = target;
            pasting = true;
            Native.ForceForeground(t);   // give focus back before hiding so Windows doesn't pick another window
            HidePopup();
            pasting = false;

            // The panel is topmost, so Windows gives focus back to the previous app asynchronously, and an app
            // that was in the background (the terminal, a browser) can take a while before it takes keys again.
            // A Ctrl+V sent before that is simply lost, which is why a paste sometimes only worked on the
            // third or fourth try (a big image makes the wait longer, so it failed more often).
            var sw = Stopwatch.StartNew();
            if (t != IntPtr.Zero)
            {
                while (Native.GetForegroundWindow() != t && sw.ElapsedMilliseconds < 1500)
                {
                    await Task.Delay(25);
                    Native.ForceForeground(t);
                }
            }
            bool back = t == IntPtr.Zero || Native.GetForegroundWindow() == t;
            if (logInfo != null)
                Log.Write("Paste: " + logInfo + "; previous window " + (back ? "took focus after " + sw.ElapsedMilliseconds + " ms" : "did NOT take focus back (" + ForegroundName() + ")"));
            if (!back) return; // pasting now would land in whatever window took the focus instead
            // A throwaway key first: it is lost if the app is still starting up, and proves it takes keys again
            // before the real one goes in.
            Native.TapDummyKey();
            await Task.Delay(40);
            if (typeText != null) Native.TypeText(typeText); else Native.SendCtrlV();
        }

        // ---------- keyboard ----------

        void OnKey(object sender, KeyEventArgs e)
        {
            if (page != Page.List)
            {
                // On the settings page or an expanded item: Esc goes back to the list.
                if (e.Key == Key.Escape) { ShowPage(Page.List); e.Handled = true; }
                else if (page == Page.Detail && e.Key == Key.Enter && detailItem != null)
                {
                    // In the text editor Enter is a new line; Ctrl+Enter pastes. Elsewhere Enter pastes.
                    bool inEditor = detailEditor != null && detailEditor.IsKeyboardFocusWithin;
                    if (!inEditor || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    {
                        SaveDetailEdits();
                        Paste(detailItem, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                        e.Handled = true;
                    }
                }
                return;
            }
            bool inSearch = search.IsKeyboardFocusWithin, inNewTag = newTag.IsKeyboardFocusWithin;
            bool emoji = emojiScroll.Visibility == Visibility.Visible;
            if (inNewTag || nameBox.IsKeyboardFocusWithin) return;
            switch (e.Key)
            {
                case Key.Escape:
                    if (view.OfType<ItemVM>().Any(v => v.ActionsOpen)) CloseActions(null);
                    else if (currentFolder != null) GoUp();
                    else HidePopup();
                    e.Handled = true;
                    break;
                case Key.Down:
                    if (inSearch && !emoji) { FocusFirst(); e.Handled = true; }
                    break;
                case Key.Up:
                    if (currentFolder == null && !inSearch && !emoji && list.SelectedIndex <= 0) { search.Focus(); e.Handled = true; }
                    break;
                case Key.Enter:
                    if (emoji) { var first = FirstVisibleEmoji(); if (first != null) InsertEmoji(first); e.Handled = true; break; }
                    var selFolder = list.SelectedItem as FolderVM;
                    if (selFolder != null) { OpenFolder(selFolder.Folder.Id); e.Handled = true; break; }
                    var vm = list.SelectedItem as ItemVM ?? view.OfType<ItemVM>().FirstOrDefault();
                    if (vm != null) Paste(vm, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    e.Handled = true;
                    break;
                case Key.Delete:
                    if (!inSearch && list.SelectedItem is ItemVM)
                    {
                        int idx = list.SelectedIndex;
                        store.Delete(((ItemVM)list.SelectedItem).Item);
                        if (view.Count > 0) { list.SelectedIndex = Math.Min(idx, view.Count - 1); FocusSelected(); }
                        e.Handled = true;
                    }
                    break;
                case Key.Back:
                    if (currentFolder != null && !inSearch) { GoUp(); e.Handled = true; }
                    break;
                case Key.Apps:
                    if (list.SelectedItem is ItemVM) { var s = (ItemVM)list.SelectedItem; CloseActions(s); s.ActionsOpen = !s.ActionsOpen; e.Handled = true; }
                    break;
            }
        }

        void FocusSelected()
        {
            list.UpdateLayout();
            var c = list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem;
            if (c != null) c.Focus();
        }

        void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            // Typing anywhere starts a search (only on the history list).
            if (page != Page.List || currentFolder != null) return;
            if (search.IsKeyboardFocusWithin || newTag.IsKeyboardFocusWithin || nameBox.IsKeyboardFocusWithin) return;
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            search.Focus();
            search.Text += e.Text;
            search.CaretIndex = search.Text.Length;
            e.Handled = true;
        }

        // ---------- emoji ----------

        void ShowEmoji(bool on)
        {
            emojiScroll.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            if (on) { emptyPanel.Visibility = Visibility.Collapsed; emojiTab.IsChecked = true; BuildEmoji(); search.Focus(); }
            else emojiTab.IsChecked = false;
        }

        int renderedCell, renderingCell;

        /// <summary>Called once at startup: renders all emoji in the background so the Emoji tab opens instantly.</summary>
        public void PreloadEmoji()
        {
            double dpiScale = Native.GetDpiForSystem() / 96.0;
            RenderEmoji((int)Math.Ceiling(28 * dpiScale * store.Settings.UiScale));
        }

        void BuildEmoji()
        {
            // Pixel size for this monitor and the Size setting; re-render only if it differs from what's cached.
            var source = PresentationSource.FromVisual(this);
            double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1.0;
            int cell = (int)Math.Ceiling(28 * scale * uiScale);
            if (renderedCell == cell) { emojiBuilt = true; FilterEmoji(); return; }
            if (!emojiBuilt)
            {
                emojiHost.Children.Clear();
                emojiHost.Children.Add(new TextBlock { Text = "Loading emoji\u2026", Foreground = (Brush)Application.Current.Resources["Fg2Brush"], Margin = new Thickness(4, 8, 0, 0) });
            }
            RenderEmoji(cell);
        }

        void RenderEmoji(int cell)
        {
            if (emojiRendering && renderingCell == cell) return;
            emojiRendering = true;
            renderingCell = cell;
            var all = EmojiData.All;
            Task.Run(() =>
            {
                try { EmojiRenderer.RenderAll(all, cell); } catch (Exception ex) { Log.Write(ex); }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    emojiRendering = false;
                    renderedCell = cell;
                    emojiBuilt = true;
                    FilterEmoji(); // also builds the grid ahead of time while the popup is hidden
                }));
            });
        }

        EmojiInfo firstEmoji;

        EmojiInfo FirstVisibleEmoji() { return firstEmoji; }

        // The full emoji grid (~400 buttons) is built once and reused, so opening the Emoji tab
        // is instant and its slide animation stays smooth. Only "Recently used" and search results
        // are rebuilt, and they're small.
        StackPanel emojiAll, emojiRecent, emojiResults;
        string recentKey;

        void FilterEmoji()
        {
            if (!emojiBuilt) return;
            string q = search.Text.Trim().ToLowerInvariant();
            var all = EmojiData.All;

            if (emojiAll == null || emojiAll.Tag as string != renderedCell.ToString())
            {
                emojiAll = new StackPanel { Tag = renderedCell.ToString() };
                foreach (var g in all.GroupBy(x => x.Category)) AddEmojiGroup(emojiAll, g.Key, g);
                recentKey = null;
            }

            UIElement[] show;
            if (q.Length == 0)
            {
                string key = string.Join(",", store.Settings.RecentEmoji);
                if (emojiRecent == null || key != recentKey)
                {
                    emojiRecent = new StackPanel();
                    var recent = store.Settings.RecentEmoji.Select(g => all.FirstOrDefault(x => x.Glyph == g)).Where(x => x != null).ToList();
                    if (recent.Count > 0) AddEmojiGroup(emojiRecent, "Recently used", recent);
                    recentKey = key;
                }
                firstEmoji = store.Settings.RecentEmoji.Count > 0 ? all.FirstOrDefault(x => x.Glyph == store.Settings.RecentEmoji[0]) : all.FirstOrDefault();
                show = new UIElement[] { emojiRecent, emojiAll };
            }
            else
            {
                var hits = all.Where(x => x.Keywords.Split(' ').Any(w => w.StartsWith(q)) || x.Keywords.Contains(q)).ToList();
                emojiResults = new StackPanel();
                AddEmojiGroup(emojiResults, hits.Count > 0 ? "Results" : "No emoji match \u201C" + q + "\u201D", hits);
                firstEmoji = hits.FirstOrDefault();
                show = new UIElement[] { emojiResults };
            }

            if (emojiHost.Children.Count != show.Length || show.Where((el, i) => emojiHost.Children[i] != el).Any())
            {
                foreach (UIElement old in emojiHost.Children.Cast<UIElement>().ToList())
                    emojiHost.Children.Remove(old); // detach so the cached panels can be re-added
                foreach (var el in show) emojiHost.Children.Add(el);
            }
        }

        void AddEmojiGroup(StackPanel host, string title, IEnumerable<EmojiInfo> items)
        {
            host.Children.Add(new TextBlock
            {
                Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["Fg2Brush"], Margin = new Thickness(4, 10, 0, 4)
            });
            var wrap = new WrapPanel();
            foreach (var em in items)
            {
                var e = em;
                object content = e.Image != null
                    ? (object)new Image { Source = e.Image, Width = 28, Height = 28 }
                    : new TextBlock { Text = e.Glyph, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 20 };
                var b = new Button { Content = content, Style = (Style)Application.Current.Resources["EmojiButton"], ToolTip = e.Keywords };
                b.Click += delegate { InsertEmoji(e); };
                wrap.Children.Add(b);
            }
            host.Children.Add(wrap);
        }

        async void InsertEmoji(EmojiInfo e)
        {
            var recent = store.Settings.RecentEmoji;
            recent.Remove(e.Glyph);
            recent.Insert(0, e.Glyph);
            if (recent.Count > 24) recent.RemoveRange(24, recent.Count - 24);
            store.SaveSettings();
            await ReturnAndPaste(e.Glyph, null);
        }
    }
}
