using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

// Without this attribute .NET Framework applies old compatibility behavior (e.g. no per-monitor DPI in WPF).
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("ClipVault")]
[assembly: AssemblyProduct("ClipVault")]
[assembly: AssemblyDescription("Clipboard history for Windows")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ClipVault
{
    public static class Program
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static Store store;
        static ClipboardService clipboard;
        static HotkeyHook hook;
        static PopupWindow popup;
        static Forms.NotifyIcon tray;
        static SettingsWindow settingsWindow;

        [STAThread]
        public static int Main(string[] args)
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\ClipVault.Singleton", out first))
            using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClipVault.Show"))
            using (var settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClipVault.Settings"))
            {
                bool wantSettings = args.Contains("--settings");
                if (!first)
                {
                    // Already running: open its history (or settings) instead of starting twice.
                    (wantSettings ? settingsSignal : showSignal).Set();
                    return 0;
                }
                try { Run(showSignal, settingsSignal, args.Contains("--startup"), wantSettings); }
                catch (Exception ex) { Log.Write(ex); MessageBox.Show(ex.Message, "ClipVault failed to start", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                return 0;
            }
        }

        static void Run(EventWaitHandle showSignal, EventWaitHandle settingsSignal, bool fromStartup, bool openSettings)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) => { Log.Write(e.Exception); e.Handled = true; };
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Styles.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(s));
            Theme.Apply();

            store = new Store();
            store.Load();
            ApplyStartup(store.Settings.StartWithWindows);

            clipboard = new ClipboardService(store);
            popup = new PopupWindow(store, clipboard);
            popup.SettingsRequested += OpenSettings;
            new WindowInteropHelper(popup).EnsureHandle();
            clipboard.Start();

            hook = new HotkeyHook();
            hook.Pressed += fg => popup.ShowAt(fg);

            SetupTray(fromStartup);
            var idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            idle.Tick += delegate { idle.Stop(); Native.TrimMemory(); };
            idle.Start();

            // A second launch (e.g. double-clicking the exe again) opens the history.
            var waiter = new Thread(() =>
            {
                var handles = new WaitHandle[] { showSignal, settingsSignal };
                while (true)
                {
                    int which = WaitHandle.WaitAny(handles);
                    if (which == 0) app.Dispatcher.BeginInvoke(new Action(() => popup.ShowAt(Native.GetForegroundWindow())));
                    else app.Dispatcher.BeginInvoke(new Action(OpenSettings));
                }
            }) { IsBackground = true };
            waiter.Start();
            if (openSettings) OpenSettings();

            // Follow light/dark and accent changes.
            SystemEvents.UserPreferenceChanged += (s, e) => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                Theme.Apply();
                if (tray != null) tray.Icon = Theme.MakeTrayIcon();
            }));

            app.Run();

            hook.Dispose();
            clipboard.Stop();
            tray.Visible = false;
            tray.Dispose();
        }

        static void SetupTray(bool quiet)
        {
            tray = new Forms.NotifyIcon { Icon = Theme.MakeTrayIcon(), Text = "ClipVault \u2014 Win+V to open", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open history  (Win+V)", null, delegate { popup.ShowAt(Native.GetForegroundWindow()); });
            var pause = new Forms.ToolStripMenuItem("Pause saving") { CheckOnClick = true };
            pause.CheckedChanged += delegate { clipboard.Paused = pause.Checked; tray.Text = pause.Checked ? "ClipVault \u2014 paused" : "ClipVault \u2014 Win+V to open"; };
            menu.Items.Add(pause);
            menu.Items.Add("Settings\u2026", null, delegate { OpenSettings(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Application.Current.Shutdown(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) popup.ShowAt(IntPtr.Zero); };
            if (!quiet)
                tray.ShowBalloonTip(4000, "ClipVault is running", "Press Win+V to open your clipboard history.", Forms.ToolTipIcon.None);
        }

        static void OpenSettings()
        {
            if (settingsWindow != null) { settingsWindow.Activate(); return; }
            settingsWindow = new SettingsWindow(store);
            settingsWindow.Closed += delegate { settingsWindow = null; };
            settingsWindow.Saved += delegate { ApplyStartup(store.Settings.StartWithWindows); store.Trim(); store.Save(); };
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        public static void ApplyStartup(bool enabled)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enabled) k.SetValue("ClipVault", "\"" + Assembly.GetExecutingAssembly().Location + "\" --startup");
                    else if (k.GetValue("ClipVault") != null) k.DeleteValue("ClipVault");
                }
            }
            catch (Exception ex) { Log.Write(ex); }
        }
    }

    public class SettingsWindow : Window
    {
        public event Action Saved;

        public SettingsWindow(Store store)
        {
            Title = "ClipVault settings";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = System.Windows.Media.Brushes.Transparent;
            UseLayoutRounding = true;

            FrameworkElement root;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Settings.xaml"))
                root = (FrameworkElement)XamlReader.Load(s);
            Content = root;

            var start = (CheckBox)root.FindName("StartWithWindows");
            var max = (TextBox)root.FindName("MaxItems");
            var folder = (TextBox)root.FindName("ClaudeFolder");
            var status = (TextBlock)root.FindName("ClaudeStatus");
            var dataInfo = (TextBlock)root.FindName("DataInfo");

            start.IsChecked = store.Settings.StartWithWindows;

            double chosenScale = store.Settings.UiScale;
            var sizeOptions = (StackPanel)root.FindName("SizeOptions");
            foreach (var pct in new[] { 100, 110, 125, 150 })
            {
                double value = pct / 100.0;
                var rb = new RadioButton
                {
                    Content = pct == 100 ? "100% (Windows)" : pct + "%",
                    GroupName = "size",
                    Style = (Style)Application.Current.Resources["Tab"],
                    IsChecked = Math.Abs(store.Settings.UiScale - value) < 0.01
                };
                rb.Checked += delegate { chosenScale = value; };
                sizeOptions.Children.Add(rb);
            }
            max.Text = store.Settings.MaxItems.ToString();
            folder.Text = store.Settings.ClaudeFolder;
            string exe = Claude.FindExe();
            status.Text = exe != null ? "Using " + exe : "Claude Code was not found on this PC.";

            long bytes = 0;
            try { bytes = new DirectoryInfo(store.Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            int n0 = store.Index.Items.Count;
            dataInfo.Text = n0 + (n0 == 1 ? " item, " : " items, ") + (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            dataInfo.ToolTip = store.Root;

            var tagList = (StackPanel)root.FindName("TagList");
            Action fillTags = null;
            fillTags = () =>
            {
                tagList.Children.Clear();
                if (store.Index.Tags.Count == 0)
                    tagList.Children.Add(new TextBlock { Text = "No tags yet. Use + next to the tabs to create one.", FontSize = 14, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Fg2Brush"] });
                foreach (var t in store.Index.Tags.ToList())
                {
                    string tag = t;
                    int count = store.Index.Items.Count(i => i.Tag == tag);
                    var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock { Text = tag + "   \u00B7   " + count + (count == 1 ? " item" : " items"), VerticalAlignment = VerticalAlignment.Center });
                    var del = new Button { Content = "\uE74D", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = "Delete tag " + tag };
                    Grid.SetColumn(del, 1);
                    del.Click += delegate { store.DeleteTag(tag); fillTags(); };
                    row.Children.Add(del);
                    tagList.Children.Add(row);
                }
            };
            fillTags();

            ((Button)root.FindName("Browse")).Click += delegate
            {
                using (var dlg = new Forms.FolderBrowserDialog { SelectedPath = folder.Text, Description = "Folder to run claude --continue in" })
                    if (dlg.ShowDialog() == Forms.DialogResult.OK) folder.Text = dlg.SelectedPath;
            };
            ((Button)root.FindName("OpenData")).Click += delegate { Process.Start("explorer.exe", Native.QuoteArg(store.Root)); };
            ((Button)root.FindName("Cancel")).Click += delegate { Close(); };
            ((Button)root.FindName("Save")).Click += delegate
            {
                int n;
                if (!int.TryParse(max.Text.Trim(), out n) || n < 10 || n > 100000)
                {
                    MessageBox.Show(this, "History size must be a number between 10 and 100000.", "ClipVault");
                    return;
                }
                store.Settings.MaxItems = n;
                store.Settings.UiScale = chosenScale;
                store.Settings.StartWithWindows = start.IsChecked == true;
                store.Settings.ClaudeFolder = folder.Text.Trim();
                store.SaveSettings();
                if (Saved != null) Saved();
                Close();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            Native.ApplyWindowEffects(h, Theme.IsDark, 2 /* Mica */);
        }
    }
}
