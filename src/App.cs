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
        static bool trayLight = Theme.TaskbarIsLight;

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
            new WindowInteropHelper(popup).EnsureHandle();
            clipboard.Start();

            hook = new HotkeyHook();
            hook.Pressed += fg => popup.ShowAt(fg);

            SetupTray(fromStartup);
            // Render emoji right after startup so the Emoji tab opens instantly.
            var preload = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            preload.Tick += delegate { preload.Stop(); popup.PreloadEmoji(); };
            preload.Start();

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
                if (tray != null)
                {
                    bool light = Theme.TaskbarIsLight;
                    if (light != trayLight) // only redraw the tray icon when the taskbar color really changed
                    {
                        trayLight = light;
                        var old = tray.Icon;
                        tray.Icon = Theme.MakeTrayIcon();
                        if (old != null) old.Dispose();
                    }
                }
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

        }

        static void OpenSettings() { popup.OpenSettings(); }

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

    /// <summary>The settings page inside the clipboard panel. Every change is saved immediately.</summary>
    public class SettingsPage
    {
        public readonly FrameworkElement Root;
        public event Action BackRequested;
        public event Action ScaleChanged;
        readonly Store store;
        readonly TextBlock dataInfo;
        readonly StackPanel tagList;
        readonly TextBox max, folder;
        TextBlock status;

        void UpdateFolderWarning()
        {
            string exe = Claude.FindExe();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            if (exe == null) status.Text = "Claude Code was not found on this PC";
            else if (string.Equals(folder.Text.Trim().TrimEnd('\\'), home, StringComparison.OrdinalIgnoreCase))
                status.Text = "Claude Code asks to trust your home folder every time. Pick another folder to avoid that.";
            else status.Text = "Claude Code found";
            status.ToolTip = exe;
        }

        public SettingsPage(Store store)
        {
            this.store = store;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Settings.xaml"))
                Root = (FrameworkElement)XamlReader.Load(s);

            var start = (CheckBox)Root.FindName("StartWithWindows");
            max = (TextBox)Root.FindName("MaxItems");
            folder = (TextBox)Root.FindName("ClaudeFolder");
            dataInfo = (TextBlock)Root.FindName("DataInfo");
            tagList = (StackPanel)Root.FindName("TagList");
            var status = (TextBlock)Root.FindName("ClaudeStatus");

            ((Button)Root.FindName("Back")).Click += delegate { if (BackRequested != null) BackRequested(); };

            start.IsChecked = store.Settings.StartWithWindows;
            start.Click += delegate
            {
                store.Settings.StartWithWindows = start.IsChecked == true;
                store.SaveSettings();
                Program.ApplyStartup(store.Settings.StartWithWindows);
            };

            // History size: saved when you press Enter or leave the box.
            max.LostKeyboardFocus += delegate { SaveMax(); };
            max.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SaveMax(); e.Handled = true; } };

            var sizeOptions = (StackPanel)Root.FindName("SizeOptions");
            foreach (var pct in new[] { 100, 110, 125, 150 })
            {
                double value = pct / 100.0;
                var rb = new RadioButton
                {
                    Content = pct + "%",
                    ToolTip = pct == 100 ? "Same size as the Windows clipboard" : null,
                    GroupName = "size",
                    Style = (Style)Application.Current.Resources["Tab"],
                    IsChecked = Math.Abs(store.Settings.UiScale - value) < 0.01
                };
                rb.Checked += delegate
                {
                    store.Settings.UiScale = value;
                    store.SaveSettings();
                    if (ScaleChanged != null) ScaleChanged();
                };
                sizeOptions.Children.Add(rb);
            }

            folder.LostKeyboardFocus += delegate { SaveFolder(); };
            folder.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SaveFolder(); e.Handled = true; } };
            ((Button)Root.FindName("Browse")).Click += delegate
            {
                using (var dlg = new Forms.FolderBrowserDialog { SelectedPath = folder.Text, Description = "Folder to run claude --continue in" })
                    if (dlg.ShowDialog() == Forms.DialogResult.OK) { folder.Text = dlg.SelectedPath; SaveFolder(); }
            };
            this.status = status;
            UpdateFolderWarning();

            ((Button)Root.FindName("OpenData")).Click += delegate { using (Process.Start("explorer.exe", Native.QuoteArg(store.Root))) { } };
        }

        /// <summary>Refreshes the values that can change while the app runs (tags, item count, size on disk).</summary>
        public void Refresh()
        {
            max.Text = store.Settings.MaxItems.ToString();
            folder.Text = store.Settings.ClaudeFolder;
            UpdateFolderWarning();

            long bytes = 0;
            try { bytes = new DirectoryInfo(store.Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            int n0 = store.Index.Items.Count;
            dataInfo.Text = n0 + (n0 == 1 ? " item, " : " items, ") + (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
            dataInfo.ToolTip = store.Root;

            tagList.Children.Clear();
            if (store.Index.Tags.Count == 0)
                tagList.Children.Add(new TextBlock { Text = "No tags yet. Use + next to the tabs.", Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Fg2Brush"] });
            foreach (var t in store.Index.Tags.ToList())
            {
                string tag = t;
                int count = store.Index.Items.Count(i => i.Tag == tag);
                var row = new Grid { Margin = new Thickness(0, 1, -6, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = tag + "   \u00B7   " + count + (count == 1 ? " item" : " items"), VerticalAlignment = VerticalAlignment.Center });
                var del = new Button { Content = "\uE74D", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = "Delete tag " + tag };
                Grid.SetColumn(del, 1);
                del.Click += delegate { store.DeleteTag(tag); Refresh(); };
                row.Children.Add(del);
                tagList.Children.Add(row);
            }
        }

        void SaveMax()
        {
            int n;
            if (int.TryParse(max.Text.Trim(), out n))
            {
                n = Math.Max(10, Math.Min(100000, n));
                if (n != store.Settings.MaxItems)
                {
                    store.Settings.MaxItems = n;
                    store.SaveSettings();
                    store.Trim();
                    store.Save();
                }
            }
            max.Text = store.Settings.MaxItems.ToString();
        }

        void SaveFolder()
        {
            string f = folder.Text.Trim();
            if (f.Length > 0 && f != store.Settings.ClaudeFolder) { store.Settings.ClaudeFolder = f; store.SaveSettings(); }
            UpdateFolderWarning();
        }
    }
}
