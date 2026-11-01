using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipVault
{
    /// <summary>A background STA thread with a message loop: the clipboard is only touched from here.</summary>
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

    /// <summary>Listens for clipboard changes, saves new content to the store, and puts items back on the clipboard.</summary>
    /// <remarks>
    /// Reading the clipboard can mean waiting for the app that copied the data to hand it over. If that app is busy
    /// or stuck, the read waits too. So all clipboard reads/writes run on a separate thread with a time limit: the
    /// panel can never freeze because of another app, and a stuck read is abandoned for a fresh thread.
    /// </remarks>
    public class ClipboardService
    {
        const string SelfFormat = "ClipVault.Internal";
        const int ReadTimeoutMs = 4000, WriteTimeoutMs = 3000;
        readonly Store store;
        readonly DispatcherTimer debounce;
        HwndSource source;
        StaWorker worker = new StaWorker("ClipVault clipboard");

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
            Native.AddClipboardFormatListener(source.Handle);
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

        static IDataObject Open()
        {
            for (int i = 0; i < 8; i++)
            {
                try { return Clipboard.GetDataObject(); }
                catch (COMException) { Thread.Sleep(30); }
                catch (ExternalException) { Thread.Sleep(30); }
            }
            return null;
        }

        static T Get<T>(IDataObject d, string format) where T : class
        {
            // Another app may still be holding the clipboard open for a moment: retry briefly before giving up.
            for (int attempt = 0; ; attempt++)
            {
                try { return d.GetDataPresent(format, false) ? d.GetData(format, false) as T : null; }
                catch (COMException ex)
                {
                    if (attempt < 6) { Thread.Sleep(40); continue; }
                    Log.Write("read " + format + ": " + ex.Message);
                    return null;
                }
                catch (Exception ex) { Log.Write("read " + format + ": " + ex.Message); return null; }
            }
        }

        /// <summary>What was on the clipboard, read on the clipboard thread (plain data, safe to hand to the UI thread).</summary>
        sealed class Snapshot
        {
            public string[] Files;
            public string Text, Html, Rtf;
            public byte[] Png;
            public BitmapSource Bitmap; // frozen
        }

        async void CaptureAsync()
        {
            try
            {
                var snap = await OnClipboardThread(() => ReadSnapshot(), ReadTimeoutMs, "read");
                if (snap == null) return;
                ClipItem added = null;
                if (snap.Files != null) added = store.AddFiles(snap.Files);
                else if (snap.Text != null) added = store.AddText(snap.Text, snap.Html, snap.Rtf);
                else { SaveImage(snap.Png, snap.Bitmap); return; }
                if (added != null && Captured != null) Captured(added);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        /// <summary>Runs on the clipboard thread.</summary>
        static Snapshot ReadSnapshot()
        {
            IDataObject d = Open();
            if (d == null) return null;

            // Respect content that asks not to be recorded (password managers, our own pastes).
            if (d.GetDataPresent(SelfFormat, false)) return null;
            if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) { Log.Write("Skipped: app asked not to be monitored"); return null; }
            if (d.GetDataPresent("Clipboard Viewer Ignore", false)) { Log.Write("Skipped: Clipboard Viewer Ignore"); return null; }
            var allow = Get<MemoryStream>(d, "CanIncludeInClipboardHistory");
            if (allow != null && allow.Length >= 4 && BitConverter.ToInt32(allow.ToArray(), 0) == 0) { Log.Write("Skipped: CanIncludeInClipboardHistory=0"); return null; }

            var files = Get<string[]>(d, DataFormats.FileDrop);
            if (files != null && files.Length > 0) return new Snapshot { Files = files };

            if (d.GetDataPresent(DataFormats.UnicodeText, true))
            {
                string text = d.GetData(DataFormats.UnicodeText, true) as string;
                if (string.IsNullOrWhiteSpace(text)) return null;
                return new Snapshot { Text = text, Html = Get<string>(d, DataFormats.Html), Rtf = Get<string>(d, DataFormats.Rtf) };
            }

            var pngStream = Get<MemoryStream>(d, "PNG");
            if (pngStream != null) return new Snapshot { Png = pngStream.ToArray() };
            BitmapSource bmp = null;
            var dib = Get<MemoryStream>(d, DataFormats.Dib);
            if (dib != null) bmp = DibToBitmap(dib.ToArray());
            else if (d.GetDataPresent(DataFormats.Bitmap, true)) bmp = Clipboard.GetImage();
            if (bmp == null)
            {
                Log.Write("Nothing usable on the clipboard. Formats: " + string.Join(" | ", d.GetFormats(false)));
                return null;
            }
            return new Snapshot { Bitmap = Detach(bmp) };
        }

        void SaveImage(byte[] png, BitmapSource bmp)
        {
            var ui = Dispatcher.CurrentDispatcher;
            // Encoding and writing large screenshots happens off the UI thread so Win+V stays instant.
            Task.Run(() =>
            {
                try
                {
                    int w, h;
                    if (png == null)
                    {
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

        /// <summary>Copies the pixels into a frozen bitmap that any thread can read.</summary>
        static BitmapSource Detach(BitmapSource src)
        {
            int stride = (src.PixelWidth * src.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * src.PixelHeight];
            src.CopyPixels(pixels, stride, 0);
            var copy = BitmapSource.Create(src.PixelWidth, src.PixelHeight, 96, 96, src.Format, src.Palette, pixels, stride);
            copy.Freeze();
            return copy;
        }

        /// <summary>Turns a CF_DIB block into a decodable .bmp by adding the missing file header.</summary>
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
                var dec = new BmpBitmapDecoder(new MemoryStream(bmp), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return dec.Frames[0];
            }
            catch (Exception ex) { Log.Write(ex); return null; }
        }

        // ---------- writing ----------

        /// <summary>Puts an item on the clipboard. Never blocks the UI thread; gives up (false) if the clipboard stays busy.</summary>
        public async Task<bool> PutAsync(ClipItem item, bool plainText)
        {
            // Gather everything from disk here; the clipboard thread only builds the data object and sets it.
            ItemKind kind = item.Kind;
            string text = null, html = null, rtf = null, path = null;
            byte[] bytes = null;
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
                    if (!plainText) bytes = File.ReadAllBytes(path);
                    break;
            }
            return await OnClipboardThread(() =>
            {
                var data = new DataObject();
                data.SetData(SelfFormat, "1");
                switch (kind)
                {
                    case ItemKind.Text:
                        data.SetData(DataFormats.UnicodeText, text);
                        if (html != null) data.SetData(DataFormats.Html, html);
                        if (rtf != null) data.SetData(DataFormats.Rtf, rtf);
                        break;
                    case ItemKind.Image:
                        // The file path goes along as text: apps that can't take a picture
                        // (Notepad, terminals, plain text boxes) paste the image's location instead.
                        data.SetData(DataFormats.UnicodeText, path);
                        if (bytes == null) break; // Shift+Enter on an image: just the path
                        var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        data.SetImage(frame);
                        data.SetData("PNG", new MemoryStream(bytes));
                        break;
                    case ItemKind.Files:
                        var list = new StringCollection();
                        list.AddRange(files);
                        data.SetFileDropList(list);
                        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // copy, not move
                        break;
                }
                for (int i = 0; i < 8; i++)
                {
                    try { Clipboard.SetDataObject(data, true); return true; }
                    catch (COMException) { Thread.Sleep(30); }
                    catch (ExternalException) { Thread.Sleep(30); }
                }
                return false;
            }, WriteTimeoutMs, "write");
        }
    }
}
