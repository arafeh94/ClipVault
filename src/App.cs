using System;
using System.Collections.Generic;
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
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

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
            // Downloaded exe: install (or update) into %LOCALAPPDATA%\Programs\ClipVault and run from there.
            if (Installer.InstallIfNeeded(args)) return 0;
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
                try { Run(showSignal, settingsSignal, args.Contains("--startup"), wantSettings, args.Contains("--installed")); }
                catch (Exception ex) { Log.Write(ex); MessageBox.Show(ex.Message, "ClipVault failed to start", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                return 0;
            }
        }

        static void Run(EventWaitHandle showSignal, EventWaitHandle settingsSignal, bool fromStartup, bool openSettings, bool justInstalled)
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
            // A newer download being installed asks this copy to exit so it can replace the exe.
            var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEvent);
            var waiter = new Thread(() =>
            {
                var handles = new WaitHandle[] { showSignal, settingsSignal, quitSignal };
                while (true)
                {
                    int which = WaitHandle.WaitAny(handles);
                    if (which == 0) app.Dispatcher.BeginInvoke(new Action(() => popup.ShowAt(Native.GetForegroundWindow())));
                    else if (which == 1) app.Dispatcher.BeginInvoke(new Action(OpenSettings));
                    else { app.Dispatcher.BeginInvoke(new Action(() => app.Shutdown())); return; }
                }
            }) { IsBackground = true };
            waiter.Start();
            if (openSettings) OpenSettings();
            if (justInstalled)
                app.Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(
                    "ClipVault is installed and running.\n\nPress Win+V to open your clipboard history.\nIt starts with Windows; right-click its tray icon for settings or to uninstall.",
                    "ClipVault", MessageBoxButton.OK, MessageBoxImage.Information)));

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
            menu.Items.Add("Uninstall ClipVault\u2026", null, delegate { AskUninstall(); });
            menu.Items.Add("Exit", null, delegate { Application.Current.Shutdown(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) popup.ShowAt(IntPtr.Zero); };

        }

        static void OpenSettings() { popup.OpenSettings(); }

        static void AskUninstall()
        {
            if (MessageBox.Show("Uninstall ClipVault?\n\nWin+V will open the Windows clipboard again.", "Uninstall ClipVault",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            int n = store.Index.Items.Count;
            bool deleteHistory = MessageBox.Show("Also delete your clipboard history (" + n + (n == 1 ? " item" : " items") + ", including pinned items and folders)?\n\nChoose No to keep it in case you reinstall.",
                "Uninstall ClipVault", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            Installer.Uninstall(deleteHistory, store.Root);
            Application.Current.Shutdown();
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
            SetupSync();
        }

        /// <summary>Refreshes the values that can change while the app runs (tags, item count, size on disk).</summary>
        public void Refresh()
        {
            RefreshSync();
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
                row.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
                // Icon button: shows the tag's icon (or a plain tag icon) and opens the icon picker.
                var iconBtn = new Button
                {
                    Content = TagIcons.Make(store.TagIcon(tag) ?? TagIcons.GlyphSpec("E8EC"), 16),
                    Style = (Style)Application.Current.Resources["IconButton"],
                    ToolTip = "Change icon",
                    Margin = new Thickness(-8, 0, 4, 0)
                };
                iconBtn.Click += delegate { TagIcons.ShowPicker(iconBtn, store, tag); };
                row.Children.Add(iconBtn);
                var label = new TextBlock { Text = tag + "   \u00B7   " + count + (count == 1 ? " item" : " items"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                var del = new Button { Content = "\uE74D", Style = (Style)Application.Current.Resources["IconButton"], ToolTip = "Delete tag " + tag };
                Grid.SetColumn(del, 2);
                del.Click += delegate { store.DeleteTag(tag); Refresh(); };
                row.Children.Add(del);
                tagList.Children.Add(row);
            }
        }

        // ---------------- sync ----------------

        SyncService sync;
        SyncPlan pendingPlan;
        bool syncBusy, changingEncryption, confirmingDelete, confirmingUnlink;

        T F<T>(string name) where T : class { return Root.FindName(name) as T; }

        void SetupSync()
        {
            sync = new SyncService(store);
            var enabled = F<CheckBox>("SyncEnabledBox");
            enabled.IsChecked = store.Settings.SyncEnabled;
            enabled.Click += delegate { store.Settings.SyncEnabled = enabled.IsChecked == true; store.SaveSettings(); RefreshSync(); };
            F<Button>("SyncSignIn").Click += delegate { SignIn(false); };
            F<Button>("SyncCreate").Click += delegate { SignIn(true); };
            F<PasswordBox>("SyncPassword").KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { SignIn(false); e.Handled = true; } };
            F<Button>("SyncChooseOk").Click += delegate { ChooseEncryption(); };
            F<Button>("SyncChooseCancel").Click += delegate { changingEncryption = false; ClearPasswords(); RefreshSync(); };
            F<Button>("SyncUnlock").Click += delegate { Unlock(); };
            F<PasswordBox>("SyncUnlockPass").KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Unlock(); e.Handled = true; } };
            // Unlink: this PC stops syncing and forgets the account and key. Items stay; the cloud and other PCs are untouched.
            F<Button>("SyncUnlockSignOut").Click += delegate { Unlink(); }; // nothing was synced yet on this PC: no need to confirm
            F<Button>("SyncSignOut").Click += delegate
            {
                pendingPlan = null;
                confirmingDelete = false;
                confirmingUnlink = true;
                F<TextBlock>("SyncPreview").Text = "Unlink this PC from " + sync.Email + "? It stops syncing and forgets the account. "
                    + "All items stay on this PC, and your cloud data and other PCs aren't affected. You can link it again any time.";
                F<Border>("SyncConfirm").Visibility = Visibility.Visible;
            };
            F<Button>("SyncPush").Click += delegate { Prepare(true); };
            F<Button>("SyncPull").Click += delegate { Prepare(false); };
            F<Button>("SyncCancel").Click += delegate { pendingPlan = null; confirmingDelete = confirmingUnlink = false; F<Border>("SyncConfirm").Visibility = Visibility.Collapsed; SyncStatus(""); };
            F<Button>("SyncConfirmButton").Click += delegate { Confirm(); };
            F<Button>("SyncChangeEnc").Click += delegate { changingEncryption = true; SyncStatus(""); RefreshSync(); };
            F<Button>("SyncDeleteAccount").Click += delegate
            {
                pendingPlan = null;
                confirmingUnlink = false;
                confirmingDelete = true;
                F<TextBlock>("SyncPreview").Text = "Delete your sync account and everything it stored in the cloud? Items on this PC stay.";
                F<Border>("SyncConfirm").Visibility = Visibility.Visible;
            };
            RefreshSync();
        }

        void RefreshSync()
        {
            if (sync == null) return;
            var s = store.Settings;
            bool on = s.SyncEnabled;
            var stage = sync.Stage;
            F<FrameworkElement>("SyncBody").Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("SyncAccountPanel").Visibility = stage == SyncStage.SignedOut ? Visibility.Visible : Visibility.Collapsed;
            bool choosing = stage == SyncStage.ChooseEncryption || (changingEncryption && stage == SyncStage.Ready);
            F<FrameworkElement>("SyncChoosePanel").Visibility = choosing ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("SyncChooseCancel").Visibility = changingEncryption ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("SyncChooseTitle").Text = changingEncryption ? "New encryption passphrase" : "Encrypt your synced data? (optional)";
            F<FrameworkElement>("SyncUnlockPanel").Visibility = stage == SyncStage.Unlock ? Visibility.Visible : Visibility.Collapsed;
            F<FrameworkElement>("SyncReadyPanel").Visibility = stage == SyncStage.Ready && !choosing ? Visibility.Visible : Visibility.Collapsed;
            if (stage == SyncStage.Ready)
            {
                F<TextBlock>("SyncWho").Text = sync.Email + "  \u00B7  " + (sync.Encrypted ? "encrypted" : "not encrypted")
                    + "\nLast push: " + When(s.LastPush) + "  \u00B7  Last pull: " + When(s.LastPull);
                F<Button>("SyncChangeEnc").Content = sync.Encrypted ? "Change passphrase" : "Turn on encryption";
            }
            if (!string.IsNullOrEmpty(s.SyncEmail) && F<TextBox>("SyncEmailBox").Text.Length == 0) F<TextBox>("SyncEmailBox").Text = s.SyncEmail;
            foreach (var b in new[] { "SyncSignIn", "SyncCreate", "SyncChooseOk", "SyncUnlock", "SyncPush", "SyncPull", "SyncSignOut", "SyncConfirmButton", "SyncChangeEnc", "SyncDeleteAccount" })
                F<Button>(b).IsEnabled = !syncBusy;
        }

        static string When(long ticks)
        {
            if (ticks == 0) return "never";
            var t = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            return t.Date == DateTime.Today ? "today " + t.ToString("HH:mm") : t.ToString("d MMM HH:mm");
        }

        void SyncStatus(string text) { F<TextBlock>("SyncStatus").Text = text; }

        void ClearPasswords()
        {
            foreach (var n in new[] { "SyncPassword", "SyncPass1", "SyncPass2", "SyncUnlockPass" }) F<PasswordBox>(n).Password = "";
        }

        async void SignIn(bool create)
        {
            string email = F<TextBox>("SyncEmailBox").Text.Trim(), password = F<PasswordBox>("SyncPassword").Password;
            if (email.Length == 0 || password.Length == 0) { SyncStatus("Enter your email and password."); return; }
            if (!await RunSync(create ? "Creating your account\u2026" : "Signing in\u2026", () => sync.SignIn(email, password, create))) return;
            ClearPasswords();
            var stage = sync.Stage;
            SyncStatus(stage == SyncStage.ChooseEncryption ? "Signed in. Choose whether to encrypt, then Push."
                : stage == SyncStage.Unlock ? "Signed in." : "Signed in. Pull to get your synced items.");
        }

        async void ChooseEncryption()
        {
            string p1 = F<PasswordBox>("SyncPass1").Password, p2 = F<PasswordBox>("SyncPass2").Password;
            if (p1 != p2) { SyncStatus("The two passphrases don't match."); return; }
            if (p1.Length > 0 && p1.Length < 8) { SyncStatus("Use at least 8 characters, or leave it empty for no encryption."); return; }
            bool changing = changingEncryption;
            if (!await RunSync(p1.Length > 0 ? "Setting up encryption\u2026" : "Saving\u2026", () => sync.ChooseEncryption(p1))) return;
            changingEncryption = false;
            ClearPasswords();
            SyncStatus((p1.Length > 0 ? "Encryption is on." : "Syncing without encryption.")
                + (changing ? " Your other PCs will ask for the new passphrase." : " Push to upload this PC's pinned text."));
            RefreshSync();
        }

        async void Unlock()
        {
            string pass = F<PasswordBox>("SyncUnlockPass").Password;
            if (pass.Length == 0) { SyncStatus("Enter your sync passphrase."); return; }
            if (!await RunSync("Checking\u2026", () => sync.Unlock(pass))) return;
            ClearPasswords();
            SyncStatus("Unlocked. Pull to get your synced items.");
        }

        void Unlink()
        {
            sync.SignOut();
            pendingPlan = null;
            changingEncryption = confirmingDelete = confirmingUnlink = false;
            F<Border>("SyncConfirm").Visibility = Visibility.Collapsed;
            SyncStatus("");
            RefreshSync();
        }

        /// <summary>Reads the cloud and shows what Push/Pull would do, waiting for Confirm.</summary>
        async void Prepare(bool push)
        {
            confirmingDelete = confirmingUnlink = false;
            F<Border>("SyncConfirm").Visibility = Visibility.Collapsed;
            Dictionary<string, RemoteDoc> remote = null;
            if (!await RunSync("Checking the cloud\u2026", () => { remote = sync.FetchRemote(); })) return;
            var plan = push ? sync.PlanPush(remote) : sync.PlanPull(remote);
            if (!plan.HasChanges)
            {
                if (push) sync.FinishPush(plan); else sync.ApplyPull(plan); // records what's already identical
                SyncStatus(plan.Summary + ". Everything is up to date.");
                RefreshSync();
                return;
            }
            pendingPlan = plan;
            F<TextBlock>("SyncPreview").Text = (push ? "Push to the cloud: " : "Pull to this PC: ") + plan.Summary + ".";
            F<Border>("SyncConfirm").Visibility = Visibility.Visible;
            SyncStatus("");
        }

        async void Confirm()
        {
            F<Border>("SyncConfirm").Visibility = Visibility.Collapsed;
            if (confirmingUnlink)
            {
                confirmingUnlink = false;
                Unlink();
                SyncStatus("This PC is unlinked. Your items are still here.");
                return;
            }
            if (confirmingDelete)
            {
                confirmingDelete = false;
                if (await RunSync("Deleting your account\u2026", () => sync.DeleteAccount())) SyncStatus("Your sync account and its cloud data were deleted.");
                return;
            }
            var plan = pendingPlan;
            if (plan == null) return;
            pendingPlan = null;
            if (plan.IsPush)
            {
                if (!await RunSync("Pushing\u2026", () => sync.ExecutePush(plan))) return;
                sync.FinishPush(plan);
                SyncStatus("Pushed: " + plan.Summary + ".");
            }
            else
            {
                sync.ApplyPull(plan);
                SyncStatus("Pulled: " + plan.Summary + ".");
            }
            RefreshSync();
        }

        /// <summary>Runs network work off the UI thread, showing progress and a readable error.</summary>
        async System.Threading.Tasks.Task<bool> RunSync(string progress, Action work)
        {
            syncBusy = true;
            RefreshSync();
            SyncStatus(progress);
            try
            {
                await System.Threading.Tasks.Task.Run(work);
                return true;
            }
            catch (SyncException ex) { SyncStatus(ex.Message); return false; }
            catch (Exception ex) { Log.Write(ex); SyncStatus("Sync failed: " + ex.Message); return false; }
            finally { syncBusy = false; RefreshSync(); }
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
