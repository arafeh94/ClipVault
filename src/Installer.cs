using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClipVault
{
    /// <summary>
    /// Makes the downloaded ClipVault.exe its own installer: run from anywhere (Downloads...), it copies itself to
    /// %LOCALAPPDATA%\Programs\ClipVault, starts from there and registers itself to start with Windows.
    /// Running a newer download replaces the installed copy. No admin rights needed.
    /// </summary>
    public static class Installer
    {
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ClipVault"); } }
        public static string Exe { get { return Path.Combine(Dir, "ClipVault.exe"); } }
        public const string QuitEvent = @"Local\ClipVault.Quit";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFile(string path);

        static bool Same(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// If this exe isn't the installed copy, installs (or updates) it, starts the installed copy and returns true:
        /// the caller should then exit. Pass --portable to run without installing.
        /// </summary>
        public static bool InstallIfNeeded(string[] args)
        {
            if (args.Contains("--portable")) return false;
            string me = Assembly.GetExecutingAssembly().Location;
            if (Same(me, Exe)) return false;
            try
            {
                bool update = File.Exists(Exe);
                StopRunningCopy();
                Directory.CreateDirectory(Dir);
                File.Copy(me, Exe, true);
                // Drop the "downloaded from the internet" mark from the installed copy, so Windows only warns
                // about the unsigned download once (when it's first opened), not at every sign-in.
                DeleteFile(Exe + ":Zone.Identifier");
                using (Process.Start(new ProcessStartInfo(Exe, update ? "--updated" : "--installed") { UseShellExecute = false })) { }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write(ex);
                System.Windows.MessageBox.Show("ClipVault couldn't be installed:\n" + ex.Message + "\n\nIt will run from its current location instead.",
                    "ClipVault", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return false;
            }
        }

        /// <summary>Asks a running ClipVault to exit (so its exe can be replaced) and waits for it.</summary>
        static void StopRunningCopy()
        {
            using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent)) quit.Set();
            for (int i = 0; i < 50; i++)
            {
                try { using (Mutex.OpenExisting(@"Local\ClipVault.Singleton")) { } }
                catch (WaitHandleCannotBeOpenedException) { return; } // no instance left
                Thread.Sleep(100);
            }
            // Still running after 5 s (e.g. an old version that doesn't know the quit signal): close it.
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("ClipVault"))
                using (p)
                {
                    if (p.Id == me) continue;
                    try { p.Kill(); p.WaitForExit(3000); } catch (Exception ex) { Log.Write(ex); }
                }
        }

        /// <summary>Removes autostart and, once ClipVault has exited, the program folder (and optionally the history).</summary>
        public static void Uninstall(bool deleteHistory, string dataDir)
        {
            Program.ApplyStartup(false);
            string cmd = "ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Dir + "\"";
            if (deleteHistory) cmd += " & rmdir /s /q \"" + dataDir + "\"";
            using (Process.Start(new ProcessStartInfo("cmd.exe", "/c " + cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })) { }
        }
    }
}
