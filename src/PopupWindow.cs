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

        public void Refresh() { Notify("Tag", "TagVis", "PinVis", "PinGlyph", "PinTip", "PinBrush", "Preview"); }

        // Card pin button: outline pin when not pinned, filled accent pin when pinned.
        public string PinGlyph { get { return Item.Pinned ? "" : ""; } }
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
        readonly ObservableCollection<ItemVM> view = new ObservableCollection<ItemVM>();
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
            Deactivated += delegate { if (!pasting && !IsOurs(Native.GetForegroundWindow())) HidePopup(); }; // our own dialogs (folder picker...) don't count

            focusWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            focusWatch.Tick += delegate { CheckFocus(); };

            store.Changed += () => Dispatcher.BeginInvoke(new Action(Rebuild));
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
            header.Visibility = listVis;
            tabsRow.Visibility = listVis;
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
                    SlideIn(header, -24, 0, 170);
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

            // Find the text cursor before showing (the lookup may take up to ~250 ms in some apps).
            opening = true;
            Anchor where;
            try { where = await CaretLocator.Find(foreground); }
            finally { opening = false; }
            var anchor = where.Point;
            bool fromCaret = where.FromText;
            int caretH = where.LineHeight;

            Theme.Apply();
            ApplyScale();
            new WindowInteropHelper(this).EnsureHandle();
            Native.ApplyWindowEffects(hwnd, Theme.IsDark, 3 /* acrylic, like the Windows clipboard panel */);

            // Reset to a clean state.
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
        void CheckFocus()
        {
            if (!IsVisible) { focusWatch.Stop(); return; }
            if (pasting) return;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOurs(fg)) return;
            if (fg != foregroundAtShow || hadFocus) HidePopup();
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
                    var info = (Native.MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (!IsOurs(Native.WindowFromPoint(info.pt)))
                        Dispatcher.BeginInvoke(new Action(() => { if (!pasting) HidePopup(); }));
                }
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        public void HidePopup()
        {
            if (!IsVisible) return;
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
            if (view.Count == 0) { search.Focus(); return; }
            list.SelectedIndex = 0;
            list.ScrollIntoView(view[0]);
            list.UpdateLayout();
            var item = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            if (item != null) item.Focus(); else list.Focus();
        }

        // ---------- tabs ----------

        void BuildTabs()
        {
            tabs.Children.Clear();
            AddTab("All", AllTab, "");       // clipboard
            AddTab("Text", TextTab, "");     // font "A"
            AddTab("Images", ImagesTab, ""); // photo
            AddTab("Files", FilesTab, "");   // folder
            foreach (var t in store.Index.Tags) AddTab(t, t, null); // your own tags keep their names
            if (currentTab != null && currentTab != AllTab && currentTab != TextTab && currentTab != ImagesTab && currentTab != FilesTab
                && !store.Index.Tags.Contains(currentTab)) currentTab = AllTab;
        }

        void AddTab(string label, string key, string glyph)
        {
            object content = glyph == null ? (object)label
                : new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 16, Margin = new Thickness(2, 0, 2, 0) };
            var rb = new RadioButton { Content = content, ToolTip = label, GroupName = "tabs", Style = (Style)Application.Current.Resources["Tab"], IsChecked = key == currentTab };
            rb.Checked += delegate { SelectTab(key); };
            tabs.Children.Add(rb);
        }

        void SelectTab(string key)
        {
            if (IsVisible && key != currentTab)
                SlideIn(Find<FrameworkElement>("Body"), TabOrder(key) > TabOrder(currentTab) ? 18 : -18, 0, 170);
            currentTab = key;
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

        // ---------- list ----------

        void Rebuild()
        {
            // Sync view models with the store.
            var live = new HashSet<string>(store.Index.Items.Select(i => i.Id));
            foreach (var dead in vms.Keys.Where(k => !live.Contains(k)).ToList()) vms.Remove(dead);
            foreach (var it in store.Index.Items) if (!vms.ContainsKey(it.Id)) vms[it.Id] = new ItemVM(it, store);

            string q = search.Text.Trim();
            var wanted = store.Index.Items
                .Where(i => InTab(i))
                .OrderBy(i => i.Pinned)              // history first, pinned items at the bottom
                .ThenByDescending(i => i.Created)
                .Select(i => vms[i.Id])
                .Where(vm => vm.Matches(q))
                .ToList();

            object selected = list.SelectedItem;
            if (!wanted.SequenceEqual(view))
            {
                // Update the list in place (remove / move / insert) instead of clearing it,
                // so cards that stay don't get redrawn and nothing jumps.
                var keep = new HashSet<ItemVM>(wanted);
                for (int i = view.Count - 1; i >= 0; i--) if (!keep.Contains(view[i])) view.RemoveAt(i);
                for (int i = 0; i < wanted.Count; i++)
                {
                    if (i < view.Count && view[i] == wanted[i]) continue;
                    int at = view.IndexOf(wanted[i]);
                    if (at >= 0) view.Move(at, i); else view.Insert(i, wanted[i]);
                }
                if (selected != null && view.Contains(selected)) list.SelectedItem = selected;
            }
            foreach (var vm in wanted) vm.Refresh();

            bool emoji = emojiScroll.Visibility == Visibility.Visible;
            emptyPanel.Visibility = !emoji && view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            emptyText.Text = q.Length > 0 ? "No matches for \u201C" + q + "\u201D."
                : currentTab == AllTab ? "Nothing here yet.\nCopy something with Ctrl+C and it will show up here."
                : store.Index.Tags.Contains(currentTab) ? "Nothing tagged " + currentTab + " yet.\nUse the pin button on an item and choose \u201C" + currentTab + "\u201D."
                : "Nothing here yet.";
        }

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
            foreach (var vm in view) if (vm != except) vm.ActionsOpen = false;
        }

        void OnCardClick(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            if (InsideButton(src)) return;
            var vm = VmOf(e.OriginalSource);
            if (vm == null) return;
            if (vm.ActionsOpen || view.Any(v => v.ActionsOpen)) { CloseActions(null); return; }
            Paste(vm, false);
        }

        void OnCardButton(object sender, RoutedEventArgs e)
        {
            var btn = e.OriginalSource as Button;
            if (btn == null) return;
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
                foreach (var other in view)
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
            if (!clipboard.Put(vm.Item, plain)) return;
            await ReturnAndPaste(null);
        }

        async Task ReturnAndPaste(string typeText)
        {
            IntPtr t = target;
            pasting = true;
            Native.ForceForeground(t);   // give focus back before hiding so Windows doesn't pick another window
            HidePopup();
            pasting = false;
            await Task.Delay(60);
            if (t != IntPtr.Zero && Native.GetForegroundWindow() != t) { Native.ForceForeground(t); await Task.Delay(40); }
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
            if (inNewTag) return;
            switch (e.Key)
            {
                case Key.Escape:
                    if (view.Any(v => v.ActionsOpen)) CloseActions(null);
                    else HidePopup();
                    e.Handled = true;
                    break;
                case Key.Down:
                    if (inSearch && !emoji) { FocusFirst(); e.Handled = true; }
                    break;
                case Key.Up:
                    if (!inSearch && !emoji && list.SelectedIndex <= 0) { search.Focus(); e.Handled = true; }
                    break;
                case Key.Enter:
                    if (emoji) { var first = FirstVisibleEmoji(); if (first != null) InsertEmoji(first); e.Handled = true; break; }
                    var vm = list.SelectedItem as ItemVM ?? (view.Count > 0 ? view[0] : null);
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
            if (page != Page.List) return;
            if (search.IsKeyboardFocusWithin || newTag.IsKeyboardFocusWithin) return;
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
            await ReturnAndPaste(e.Glyph);
        }
    }
}
