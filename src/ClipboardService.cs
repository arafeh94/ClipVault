using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipVault
{
    /// <summary>A background thread with a message loop: the clipboard is only read and written from here.</summary>
    sealed class StaWorker
    {
        Dispatcher dispatcher;

        public StaWorker(string name)
        {
            var ready = new ManualResetEventSlim();
            var t = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true, Name = name };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            ready.Wait();
        }

        public Task<T> Run<T>(Func<T> f) { return dispatcher.InvokeAsync(f).Task; }
        public void Stop() { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
    }

    /// <summary>The plain Win32 clipboard functions (no OLE): immediate data, no delayed rendering, no COM calls.</summary>
    static class Win32Clipboard
    {
        public const uint CF_DIB = 8, CF_UNICODETEXT = 13, CF_HDROP = 15;
        const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
        [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
        [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, StringBuilder name, int max);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder file, uint max);

        public static uint Format(string name) { return RegisterClipboardFormat(name); }
        public static bool Has(uint format) { return IsClipboardFormatAvailable(format); }

        /// <summary>Opens the clipboard, retrying briefly while another app has it open.</summary>
        public static bool Open(IntPtr owner)
        {
            for (int i = 0; i < 25; i++)
            {
                if (OpenClipboard(owner)) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        public static byte[] GetBytes(uint format)
        {
            IntPtr h = GetClipboardData(format);
            if (h == IntPtr.Zero) return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                var data = new byte[(long)GlobalSize(h)];
                Marshal.Copy(p, data, 0, data.Length);
                return data;
            }
            finally { GlobalUnlock(h); }
        }

        /// <summary>Text stored up to its terminating zero.</summary>
        public static string GetString(uint format, Encoding enc)
        {
            var b = GetBytes(format);
            if (b == null) return null;
            string s = enc.GetString(b);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        public static string[] GetFiles()
        {
            IntPtr h = GetClipboardData(CF_HDROP);
            if (h == IntPtr.Zero) return null;
            uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
            var files = new string[n];
            for (uint i = 0; i < n; i++)
            {
                var sb = new StringBuilder((int)DragQueryFile(h, i, null, 0) + 1);
                DragQueryFile(h, i, sb, (uint)sb.Capacity);
                files[i] = sb.ToString();
            }
            return files;
        }

        public static bool SetBytes(uint format, byte[] data)
        {
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE, new UIntPtr((uint)Math.Max(1, data.Length)));
            if (h == IntPtr.Zero) return false;
            IntPtr p = GlobalLock(h);
            Marshal.Copy(data, 0, p, data.Length);
            GlobalUnlock(h);
            if (SetClipboardData(format, h) != IntPtr.Zero) return true; // the clipboard owns the memory now
            GlobalFree(h);
            return false;
        }

        public static bool SetString(uint format, string s, Encoding enc) { return SetBytes(format, enc.GetBytes(s + "\0")); }

        /// <summary>CF_HDROP: a DROPFILES header followed by zero-separated wide paths and a final zero.</summary>
        public static bool SetFiles(string[] files)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(20); w.Write(0); w.Write(0); w.Write(0); w.Write(1); // pFiles, pt.x, pt.y, fNC, fWide
            foreach (var f in files) w.Write(Encoding.Unicode.GetBytes(f + "\0"));
            w.Write((short)0);
            return SetBytes(CF_HDROP, ms.ToArray());
        }

        public static string FormatNames()
        {
            var names = new StringBuilder();
            for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f))
            {
                var sb = new StringBuilder(128);
                names.Append(GetClipboardFormatName(f, sb, sb.Capacity) > 0 ? sb.ToString() : "#" + f).Append(" | ");
            }
            return names.ToString();
        }
    }

    /// <summary>Listens for clipboard changes, saves new content to the store, and puts items back on the clipboard.</summary>
    /// <remarks>
    /// Reading the clipboard can mean waiting for the app that copied the data, so reads/writes run on a separate
    /// thread with a time limit and the panel can never freeze. The clipboard's owner is always ClipVault's
    /// listener window on the UI thread (never the worker), and all data is placed immediately, so a slow or
    /// abandoned worker can never block later copies and pastes.
    /// </remarks>
    public class ClipboardService
    {
        const string SelfFormat = "ClipVault.Internal";
        const int ReadTimeoutMs = 4000, WriteTimeoutMs = 3000;
        static readonly uint FmtSelf = Win32Clipboard.Format(SelfFormat);
        static readonly uint FmtHtml = Win32Clipboard.Format("HTML Format");
        static readonly uint FmtRtf = Win32Clipboard.Format("Rich Text Format");
        static readonly uint FmtPng = Win32Clipboard.Format("PNG");
        static readonly uint FmtDropEffect = Win32Clipboard.Format("Preferred DropEffect");
        static readonly uint FmtExclude = Win32Clipboard.Format("ExcludeClipboardContentFromMonitorProcessing");
        static readonly uint FmtViewerIgnore = Win32Clipboard.Format("Clipboard Viewer Ignore");
        static readonly uint FmtCanInclude = Win32Clipboard.Format("CanIncludeInClipboardHistory");

        readonly Store store;
        readonly DispatcherTimer debounce;
        HwndSource source;
        IntPtr owner; // the listener window (UI thread): owns what ClipVault puts on the clipboard
        StaWorker worker = new StaWorker("ClipVault clipboard");

        public bool Paused;
        public event Action<ClipItem> Captured;

        public ClipboardService(Store store)
        {
            this.store = store;
            // Apps often write several formats in quick succession; wait for them to finish.
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            debounce.Tick += delegate { debounce.Stop(); CaptureAsync(); };
        }

        public void Start()
        {
            var p = new HwndSourceParameters("ClipVaultListener") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
            source = new HwndSource(p);
            source.AddHook(WndProc);
            owner = source.Handle;
            Native.AddClipboardFormatListener(owner);
        }

        public void Stop()
        {
            if (source == null) return;
            Native.RemoveClipboardFormatListener(source.Handle);
            source.Dispose();
            source = null;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_CLIPBOARDUPDATE && !Paused) { debounce.Stop(); debounce.Start(); }
            return IntPtr.Zero;
        }

        /// <summary>Runs f on the clipboard thread. Gives up after the time limit (returns default) and replaces a stuck thread.</summary>
        async Task<T> OnClipboardThread<T>(Func<T> f, int timeoutMs, string what)
        {
            var w = worker;
            var task = w.Run(f);
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task) return await task;
            Log.Write("Clipboard " + what + " took over " + timeoutMs + " ms (another app is holding the clipboard). Skipped it and started a fresh clipboard thread.");
            if (worker == w) worker = new StaWorker("ClipVault clipboard");
            var ignored = task.ContinueWith(_ => w.Stop()); // retire the stuck thread whenever it finishes
            return default(T);
        }

        // ---------- reading ----------

        /// <summary>What was on the clipboard (plain data, safe to hand between threads).</summary>
        sealed class Snapshot
        {
            public string[] Files;
            public string Text, Html, Rtf;
            public byte[] Png, Dib;
        }

        async void CaptureAsync()
        {
            try
            {
                IntPtr o = owner;
                var snap = await OnClipboardThread(() => ReadSnapshot(o), ReadTimeoutMs, "read");
                if (snap == null) return;
                ClipItem added = null;
                if (snap.Files != null) added = store.AddFiles(snap.Files);
                else if (snap.Text != null) added = store.AddText(snap.Text, snap.Html, snap.Rtf);
                else { SaveImage(snap.Png, snap.Dib); return; }
                if (added != null && Captured != null) Captured(added);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>Runs on the clipboard thread. Only copies bytes out; decoding happens after the clipboard is closed.</summary>
        static Snapshot ReadSnapshot(IntPtr owner)
        {
            if (!Win32Clipboard.Open(owner)) { Log.Write("Couldn't open the clipboard to save a copy (another app kept it open)."); return null; }
            try
            {
                // Respect content that asks not to be recorded (password managers, our own pastes).
                if (Win32Clipboard.Has(FmtSelf)) return null;
                if (Win32Clipboard.Has(FmtExclude)) { Log.Write("Skipped: app asked not to be monitored"); return null; }
                if (Win32Clipboard.Has(FmtViewerIgnore)) { Log.Write("Skipped: Clipboard Viewer Ignore"); return null; }
                if (Win32Clipboard.Has(FmtCanInclude))
                {
                    var allow = Win32Clipboard.GetBytes(FmtCanInclude);
                    if (allow != null && allow.Length >= 4 && BitConverter.ToInt32(allow, 0) == 0) { Log.Write("Skipped: CanIncludeInClipboardHistory=0"); return null; }
                }

                if (Win32Clipboard.Has(Win32Clipboard.CF_HDROP))
                {
                    var files = Win32Clipboard.GetFiles();
                    if (files != null && files.Length > 0) return new Snapshot { Files = files };
                }
                if (Win32Clipboard.Has(Win32Clipboard.CF_UNICODETEXT))
                {
                    string text = Win32Clipboard.GetString(Win32Clipboard.CF_UNICODETEXT, Encoding.Unicode);
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    return new Snapshot
                    {
                        Text = text,
                        Html = Win32Clipboard.Has(FmtHtml) ? Win32Clipboard.GetString(FmtHtml, Encoding.UTF8) : null,
                        Rtf = Win32Clipboard.Has(FmtRtf) ? Win32Clipboard.GetString(FmtRtf, Encoding.Default) : null
                    };
                }
                if (Win32Clipboard.Has(FmtPng)) return new Snapshot { Png = Win32Clipboard.GetBytes(FmtPng) };
                if (Win32Clipboard.Has(Win32Clipboard.CF_DIB)) return new Snapshot { Dib = Win32Clipboard.GetBytes(Win32Clipboard.CF_DIB) };
                Log.Write("Nothing usable on the clipboard. Formats: " + Win32Clipboard.FormatNames());
                return null;
            }
            finally { Win32Clipboard.CloseClipboard(); }
        }

        void SaveImage(byte[] png, byte[] dib)
        {
            if (png == null && dib == null) return;
            var ui = Dispatcher.CurrentDispatcher;
            // Decoding, encoding and writing large screenshots happens off the UI thread so Win+V stays instant.
            Task.Run(() =>
            {
                try
                {
                    int w, h;
                    if (png == null)
                    {
                        var bmp = DibToBitmap(dib);
                        if (bmp == null) return;
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bmp));
                        using (var ms = new MemoryStream()) { enc.Save(ms); png = ms.ToArray(); }
                        w = bmp.PixelWidth; h = bmp.PixelHeight;
                    }
                    else
                    {
                        using (var ms = new MemoryStream(png))
                        {
                            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                            w = frame.PixelWidth; h = frame.PixelHeight;
                        }
                    }
                    var prepared = store.PrepareImage(png, w, h);
                    ui.BeginInvoke(new Action(() =>
                    {
                        var item = store.AddPreparedImage(prepared);
                        if (Captured != null) Captured(item);
                    }));
                }
                catch (Exception ex) { Log.Write(ex); }
            });
        }

        /// <summary>Turns a CF_DIB block into a bitmap by adding the missing .bmp file header.</summary>
        static BitmapSource DibToBitmap(byte[] dib)
        {
            try
            {
                int headerSize = BitConverter.ToInt32(dib, 0);
                int bpp = BitConverter.ToInt16(dib, 14);
                int compression = BitConverter.ToInt32(dib, 16);
                int colorsUsed = BitConverter.ToInt32(dib, 32);
                int masks = (compression == 3 && headerSize == 40) ? 12 : 0;
                int palette = colorsUsed != 0 ? colorsUsed * 4 : (bpp <= 8 ? (1 << bpp) * 4 : 0);
                int offset = 14 + headerSize + masks + palette;
                var bmp = new byte[14 + dib.Length];
                bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
                BitConverter.GetBytes(offset).CopyTo(bmp, 10);
                Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
                var frame = new BmpBitmapDecoder(new MemoryStream(bmp), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                return frame;
            }
            catch (Exception ex) { Log.Write(ex); return null; }
        }

        /// <summary>A 32-bit bottom-up CF_DIB for apps that don't read PNG from the clipboard.</summary>
        static byte[] PngToDib(byte[] png)
        {
            var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            int w = bgr.PixelWidth, h = bgr.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgr.CopyPixels(px, stride, 0);
            var dib = new byte[40 + px.Length];
            using (var bw = new BinaryWriter(new MemoryStream(dib)))
            {
                bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)32); bw.Write(0);
                bw.Write(px.Length); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
                for (int y = h - 1; y >= 0; y--) bw.Write(px, y * stride, stride); // DIBs are stored bottom-up
            }
            return dib;
        }

        // ---------- writing ----------

        /// <summary>Puts an item on the clipboard. Never blocks the UI thread; gives up (false) if the clipboard stays busy.</summary>
        public async Task<bool> PutAsync(ClipItem item, bool plainText)
        {
            // Gather everything from disk here; the clipboard thread only converts and sets it.
            ItemKind kind = item.Kind;
            string text = null, html = null, rtf = null, path = null;
            byte[] png = null;
            string[] files = item.Files;
            switch (kind)
            {
                case ItemKind.Text:
                    text = store.GetText(item);
                    if (!plainText)
                    {
                        if (item.HasHtml) html = store.GetExtra(item, ".html");
                        if (item.HasRtf) rtf = store.GetExtra(item, ".rtf");
                    }
                    break;
                case ItemKind.Image:
                    path = store.FilePath(item, ".png");
                    if (!File.Exists(path)) return false;
                    if (!plainText) png = File.ReadAllBytes(path);
                    break;
            }
            IntPtr o = owner;
            return await OnClipboardThread(() =>
            {
                byte[] dib = png != null ? PngToDib(png) : null; // convert before opening the clipboard, to hold it briefly
                if (!Win32Clipboard.Open(o)) return false;
                try
                {
                    Win32Clipboard.EmptyClipboard(); // ClipVault's listener window becomes the owner
                    Win32Clipboard.SetBytes(FmtSelf, new byte[] { 1 });
                    switch (kind)
                    {
                        case ItemKind.Text:
                            Win32Clipboard.SetString(Win32Clipboard.CF_UNICODETEXT, text ?? "", Encoding.Unicode);
                            if (html != null) Win32Clipboard.SetString(FmtHtml, html, Encoding.UTF8);
                            if (rtf != null) Win32Clipboard.SetString(FmtRtf, rtf, Encoding.Default);
                            break;
                        case ItemKind.Image:
                            // The file path goes along as text: apps that can't take a picture
                            // (Notepad, terminals, plain text boxes) paste the image's location instead.
                            Win32Clipboard.SetString(Win32Clipboard.CF_UNICODETEXT, path, Encoding.Unicode);
                            if (png != null)
                            {
                                Win32Clipboard.SetBytes(FmtPng, png);
                                Win32Clipboard.SetBytes(Win32Clipboard.CF_DIB, dib);
                            }
                            break;
                        case ItemKind.Files:
                            Win32Clipboard.SetFiles(files);
                            Win32Clipboard.SetBytes(FmtDropEffect, BitConverter.GetBytes(1)); // copy, not move
                            break;
                    }
                    // Setting a format can fail (out of memory for a big DIB, clipboard torn down underneath us).
                    // Reporting success then would send a paste with nothing to paste.
                    if (kind == ItemKind.Image && png != null && !Win32Clipboard.Has(Win32Clipboard.CF_DIB))
                    {
                        Log.Write("Paste: the picture did not make it onto the clipboard");
                        return false;
                    }
                    return true;
                }
                finally { Win32Clipboard.CloseClipboard(); }
            }, WriteTimeoutMs, "write");
        }
    }
}
