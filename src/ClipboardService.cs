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
    /// <summary>Listens for clipboard changes, saves new content to the store, and puts items back on the clipboard.</summary>
    public class ClipboardService
    {
        const string SelfFormat = "ClipVault.Internal";
        readonly Store store;
        readonly DispatcherTimer debounce;
        HwndSource source;

        public bool Paused;
        public event Action<ClipItem> Captured;

        public ClipboardService(Store store)
        {
            this.store = store;
            // Apps often write several formats in quick succession; wait for them to finish.
            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            debounce.Tick += delegate { debounce.Stop(); Capture(); };
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

        void Capture()
        {
            try
            {
                IDataObject d = Open();
                if (d == null) return;

                // Respect content that asks not to be recorded (password managers, our own pastes).
                if (d.GetDataPresent(SelfFormat, false)) return;
                if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) { Log.Write("Skipped: app asked not to be monitored"); return; }
                if (d.GetDataPresent("Clipboard Viewer Ignore", false)) { Log.Write("Skipped: Clipboard Viewer Ignore"); return; }
                var allow = Get<MemoryStream>(d, "CanIncludeInClipboardHistory");
                if (allow != null && allow.Length >= 4 && BitConverter.ToInt32(allow.ToArray(), 0) == 0) { Log.Write("Skipped: CanIncludeInClipboardHistory=0"); return; }

                ClipItem added = null;
                var files = Get<string[]>(d, DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    added = store.AddFiles(files);
                }
                else if (d.GetDataPresent(DataFormats.UnicodeText, true))
                {
                    string text = d.GetData(DataFormats.UnicodeText, true) as string;
                    if (string.IsNullOrWhiteSpace(text)) return;
                    added = store.AddText(text, Get<string>(d, DataFormats.Html), Get<string>(d, DataFormats.Rtf));
                }
                else
                {
                    CaptureImage(d);
                    return;
                }
                if (added != null && Captured != null) Captured(added);
            }
            catch (Exception ex) { Log.Write(ex); }
        }

        void CaptureImage(IDataObject d)
        {
            byte[] png = null;
            BitmapSource bmp = null;

            var pngStream = Get<MemoryStream>(d, "PNG");
            if (pngStream != null) png = pngStream.ToArray();
            else
            {
                var dib = Get<MemoryStream>(d, DataFormats.Dib);
                if (dib != null) bmp = DibToBitmap(dib.ToArray());
                else if (d.GetDataPresent(DataFormats.Bitmap, true)) bmp = Clipboard.GetImage();
            }
            if (png == null && bmp == null)
            {
                Log.Write("Nothing usable on the clipboard. Formats: " + string.Join(" | ", d.GetFormats(false)));
                return;
            }
            if (bmp != null) bmp = Detach(bmp);

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

        public bool Put(ClipItem item, bool plainText)
        {
            var data = new DataObject();
            data.SetData(SelfFormat, "1");
            switch (item.Kind)
            {
                case ItemKind.Text:
                    data.SetData(DataFormats.UnicodeText, store.GetText(item));
                    if (!plainText)
                    {
                        if (item.HasHtml) { var h = store.GetExtra(item, ".html"); if (h != null) data.SetData(DataFormats.Html, h); }
                        if (item.HasRtf) { var r = store.GetExtra(item, ".rtf"); if (r != null) data.SetData(DataFormats.Rtf, r); }
                    }
                    break;
                case ItemKind.Image:
                    string path = store.FilePath(item, ".png");
                    if (!File.Exists(path)) return false;
                    // The file path goes along as text: apps that can't take a picture
                    // (Notepad, terminals, plain text boxes) paste the image's location instead.
                    data.SetData(DataFormats.UnicodeText, path);
                    if (plainText) break; // Shift+Enter on an image: just the path
                    byte[] bytes = File.ReadAllBytes(path);
                    var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    data.SetImage(frame);
                    data.SetData("PNG", new MemoryStream(bytes));
                    break;
                case ItemKind.Files:
                    var list = new StringCollection();
                    list.AddRange(item.Files);
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
        }
    }
}
