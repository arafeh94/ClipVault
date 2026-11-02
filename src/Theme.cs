using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClipVault
{
    /// <summary>Reads light/dark mode and the accent color from Windows and publishes them as app-wide brushes.</summary>
    public static class Theme
    {
        public static bool IsDark { get; private set; }
        static string appliedKey;

        static int ReadDword(string key, string name, int fallback)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : fallback;
                }
            }
            catch { return fallback; }
        }

        public static bool TaskbarIsLight
        {
            get { return ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) == 1; }
        }

        static Color Accent()
        {
            int abgr = ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor", unchecked((int)0xFFD47800));
            return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }

        static Color C(uint argb) { return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb); }

        static void Set(ResourceDictionary r, string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            r[key] = b;
        }

        public static void Apply()
        {
            IsDark = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;
            var r = Application.Current.Resources;
            Color accent = Accent();
            string key = IsDark + "|" + accent;
            if (key == appliedKey) return; // nothing changed: skip rebuilding every brush (runs on each Win+V)
            appliedKey = key;
            // Accent for text/indicators: Windows uses a lighter shade on dark, darker shade on light.
            Color accentText = IsDark ? Mix(accent, Colors.White, 0.45) : Mix(accent, Colors.Black, 0.15);
            Set(r, "AccentBrush", accentText);
            Set(r, "AccentFillBrush", accent);
            if (IsDark)
            {
                Set(r, "TintBrush", C(0xE0181818)); // a deep tint over the acrylic backdrop (panel shows about #1F1F1F)
                Set(r, "FgBrush", C(0xFFFFFFFF));
                Set(r, "Fg2Brush", C(0xC8FFFFFF));
                Set(r, "Fg3Brush", C(0x8BFFFFFF));
                Set(r, "CardBrush", C(0x0DFFFFFF));
                Set(r, "CardHoverBrush", C(0x15FFFFFF));
                Set(r, "CardBorderBrush", C(0x19000000));
                Set(r, "CardSolidBrush", C(0xFF2C2C2C));
                Set(r, "HoverBrush", C(0x15FFFFFF));
                Set(r, "PressedBrush", C(0x0BFFFFFF));
                Set(r, "InputBrush", C(0x0FFFFFFF));
                Set(r, "InputFocusBrush", C(0xB31E1E1E));
                Set(r, "InputBorderBrush", C(0x12FFFFFF));
                // Menus, pickers and tooltips use the same color the panel shows on screen (acrylic + tint),
                // so pop-ups look like part of the panel instead of a darker box.
                Set(r, "MenuBrush", C(0xFF1F1F1F)); // same as the panel on screen, so menus and pickers blend in
                Set(r, "MenuBorderBrush", C(0x1FFFFFFF));
                Set(r, "ThumbBrush", C(0x8BFFFFFF));
                Set(r, "TagBrush", C(0x1AFFFFFF));
                Set(r, "DividerBrush", C(0x15FFFFFF));
            }
            else
            {
                Set(r, "TintBrush", C(0x80F3F3F3));
                Set(r, "FgBrush", C(0xE4000000));
                Set(r, "Fg2Brush", C(0x9E000000));
                Set(r, "Fg3Brush", C(0x72000000));
                Set(r, "CardBrush", C(0xB3FFFFFF));
                Set(r, "CardHoverBrush", C(0xFFFBFBFB));
                Set(r, "CardBorderBrush", C(0x0F000000));
                Set(r, "CardSolidBrush", C(0xFFFBFBFB));
                Set(r, "HoverBrush", C(0x09000000));
                Set(r, "PressedBrush", C(0x06000000));
                Set(r, "InputBrush", C(0xB3FFFFFF));
                Set(r, "InputFocusBrush", C(0xFFFFFFFF));
                Set(r, "InputBorderBrush", C(0x0F000000));
                Set(r, "MenuBrush", C(0xFFF0F0F0));
                Set(r, "MenuBorderBrush", C(0x1F000000));
                Set(r, "ThumbBrush", C(0x72000000));
                Set(r, "TagBrush", C(0x0F000000));
                Set(r, "DividerBrush", C(0x0F000000));
            }
            // Same color as the other icons (white on dark, black on light) instead of a brand color.
            r["ClaudeIcon"] = MakeClaudeIcon(IsDark ? Colors.White : Color.FromArgb(0xE4, 0, 0, 0));
        }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }


        /// <summary>A Claude-style sparkle in the given color, drawn as vectors (no logo file is bundled).</summary>
        public static ImageSource MakeClaudeIcon(Color color)
        {
            {
                var group = new DrawingGroup();
                var pen = new Pen(new SolidColorBrush(color), 1.25) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var geo = new GeometryGroup();
                double[] lengths = { 7.2, 5.6, 6.6, 5.2, 7.0, 5.8, 6.4, 5.4, 7.1, 5.5, 6.8, 5.3 };
                for (int i = 0; i < lengths.Length; i++)
                {
                    double a = (i * 30 + 8) * Math.PI / 180;
                    geo.Children.Add(new LineGeometry(new Point(8 + Math.Cos(a) * 2.6, 8 + Math.Sin(a) * 2.6),
                                                      new Point(8 + Math.Cos(a) * lengths[i], 8 + Math.Sin(a) * lengths[i])));
                }
                group.Children.Add(new GeometryDrawing(null, pen, geo));
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
                var img = new DrawingImage(group);
                img.Freeze();
                return img;
            }
        }

        /// <summary>Tray icon: the Fluent "paste" glyph, white or black to match the taskbar.</summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static System.Drawing.Icon MakeTrayIcon()
        {
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                string family = System.Drawing.FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
                using (var font = new System.Drawing.Font(family, 20, System.Drawing.GraphicsUnit.Pixel))
                using (var brush = new System.Drawing.SolidBrush(TaskbarIsLight ? System.Drawing.Color.Black : System.Drawing.Color.White))
                {
                    var sf = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
                    g.DrawString("\uE77F", font, brush, new System.Drawing.RectangleF(0, 1, 32, 32), sf);
                }
                // GetHicon creates a GDI icon that .NET never frees: copy it, then destroy the original.
                IntPtr h = bmp.GetHicon();
                try { using (var tmp = System.Drawing.Icon.FromHandle(h)) return (System.Drawing.Icon)tmp.Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }

    /// <summary>Sends an item to the most recent Claude Code conversation: claude --continue "&lt;item&gt;".</summary>
    public static class Claude
    {
        public static string FindExe()
        {
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Trim().Length > 0).ToList();
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".local\bin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            foreach (string raw in dirs)
            {
                try
                {
                    string d = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                    string exe = Path.Combine(d, "claude.exe");
                    if (File.Exists(exe)) return exe;
                    // npm install: claude.cmd forwards to node_modules\@anthropic-ai\claude-code\bin\claude.exe
                    exe = Path.Combine(d, @"node_modules\@anthropic-ai\claude-code\bin\claude.exe");
                    if (File.Exists(Path.Combine(d, "claude.cmd")) && File.Exists(exe)) return exe;
                }
                catch { }
            }
            return null;
        }

        public static string Send(string prompt, string folder)
        {
            string exe = FindExe();
            if (exe == null) return "Claude Code (claude.exe) was not found. Install it, or make sure it is on your PATH.";
            try { Directory.CreateDirectory(folder); } // e.g. the default ClaudeChat folder on first use
            catch { folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            if (prompt.Length > 28000)
            {
                // Windows command lines are limited to 32K characters; hand over long content as a file.
                string dir = Path.Combine(Path.GetTempPath(), "ClipVault");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "clipboard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(file, prompt);
                prompt = "The clipboard content is too long to paste here, so it was saved to this file: " + file;
            }
            var psi = new ProcessStartInfo(exe, "--continue " + Native.QuoteArg(prompt))
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = folder
            };
            using (Process.Start(psi)) { } // we don't wait on it; release the handle right away
            return null;
        }
    }
}
