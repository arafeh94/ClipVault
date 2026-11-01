using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipVault
{
    public class EmojiInfo
    {
        public string Glyph;
        public string Keywords;
        public string Category;
        public ImageSource Image;
    }

    public static class EmojiData
    {
        static List<EmojiInfo> all;

        /// <summary>Emoji list from the embedded Emojis.txt ("#Category" lines, then "emoji keywords...").</summary>
        public static List<EmojiInfo> All
        {
            get
            {
                if (all != null) return all;
                all = new List<EmojiInfo>();
                string category = "";
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Emojis.txt"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0) continue;
                        if (line[0] == '#' && line.Length > 1 && char.IsLetter(line[1])) { category = line.Substring(1).Trim(); continue; }
                        int sp = line.IndexOf(' ');
                        string glyph = sp < 0 ? line : line.Substring(0, sp);
                        string words = sp < 0 ? "" : line.Substring(sp + 1);
                        all.Add(new EmojiInfo { Glyph = glyph, Keywords = words.ToLowerInvariant(), Category = category });
                    }
                }
                return all;
            }
        }
    }

    /// <summary>
    /// Draws color emoji with Direct2D + DirectWrite (WPF on .NET Framework can only draw them in black and white).
    /// All emoji go into one atlas bitmap; each EmojiInfo gets a cropped view of it.
    /// </summary>
    public static class EmojiRenderer
    {
        [DllImport("d2d1.dll")] static extern int D2D1CreateFactory(int type, ref Guid riid, IntPtr opts, out IntPtr factory);
        [DllImport("dwrite.dll")] static extern int DWriteCreateFactory(int type, ref Guid riid, out IntPtr factory);
        [DllImport("ole32.dll")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int ctx, ref Guid iid, out IntPtr obj);

        [StructLayout(LayoutKind.Sequential)] struct RenderTargetProps { public int type, format, alpha; public float dpiX, dpiY; public int usage, minLevel; }
        [StructLayout(LayoutKind.Sequential)] struct ColorF { public float r, g, b, a; }
        [StructLayout(LayoutKind.Sequential)] struct RectF { public float l, t, r, b; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBitmapFn(IntPtr self, uint w, uint h, ref Guid fmt, int opt, out IntPtr bmp);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateWicTargetFn(IntPtr self, IntPtr bmp, ref RenderTargetProps p, out IntPtr rt);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTextFormatFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection, int weight, int style, int stretch, float size, [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetIntFn(IntPtr self, int v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBrushFn(IntPtr self, ref ColorF c, IntPtr props, out IntPtr brush);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void VoidFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void VoidIntFn(IntPtr self, int v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearFn(IntPtr self, ref ColorF c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EndDrawFn(IntPtr self, out ulong tag1, out ulong tag2);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DrawTextFn(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string text, uint len, IntPtr format, ref RectF rect, IntPtr brush, int options, int measuring);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CopyPixelsFn(IntPtr self, IntPtr rect, uint stride, uint size, byte[] buffer);

        static T Fn<T>(IntPtr obj, int slot)
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size), typeof(T));
        }

        static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Fn<ReleaseFn>(obj, 2)(obj); }

        static void Check(int hr, string what) { if (hr < 0) throw new COMException(what, hr); }

        /// <summary>Renders every emoji at <paramref name="cell"/> pixels. Safe to call from a background thread.</summary>
        public static void RenderAll(IList<EmojiInfo> emoji, int cell)
        {
            Guid iidD2D = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
            Guid iidDWrite = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
            Guid clsidWic = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
            Guid iidWic = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            Guid pbgra = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc910");

            int cols = 32, rows = (emoji.Count + cols - 1) / cols;
            int width = cols * cell, height = Math.Max(1, rows) * cell;
            IntPtr d2d = IntPtr.Zero, dwrite = IntPtr.Zero, wic = IntPtr.Zero, bmp = IntPtr.Zero, rt = IntPtr.Zero, fmt = IntPtr.Zero, brush = IntPtr.Zero;
            try
            {
                Check(D2D1CreateFactory(0, ref iidD2D, IntPtr.Zero, out d2d), "D2D1CreateFactory");
                Check(DWriteCreateFactory(0, ref iidDWrite, out dwrite), "DWriteCreateFactory");
                Check(CoCreateInstance(ref clsidWic, IntPtr.Zero, 1, ref iidWic, out wic), "WIC");
                Check(Fn<CreateBitmapFn>(wic, 17)(wic, (uint)width, (uint)height, ref pbgra, 1, out bmp), "CreateBitmap");
                var props = new RenderTargetProps { format = 87 /* B8G8R8A8 */, alpha = 1 /* premultiplied */, dpiX = 96, dpiY = 96 };
                Check(Fn<CreateWicTargetFn>(d2d, 13)(d2d, bmp, ref props, out rt), "CreateWicBitmapRenderTarget");
                Check(Fn<CreateTextFormatFn>(dwrite, 15)(dwrite, "Segoe UI Emoji", IntPtr.Zero, 400, 0, 5, cell * 0.72f, "en-us", out fmt), "CreateTextFormat");
                Fn<SetIntFn>(fmt, 3)(fmt, 2); // centered horizontally
                Fn<SetIntFn>(fmt, 4)(fmt, 2); // centered vertically
                var black = new ColorF { a = 1 };
                Check(Fn<CreateBrushFn>(rt, 8)(rt, ref black, IntPtr.Zero, out brush), "CreateSolidColorBrush");

                Fn<VoidIntFn>(rt, 34)(rt, 2); // grayscale text antialiasing (transparent target)
                Fn<VoidFn>(rt, 48)(rt);       // BeginDraw
                var clear = new ColorF();
                Fn<ClearFn>(rt, 47)(rt, ref clear);
                var draw = Fn<DrawTextFn>(rt, 27);
                for (int i = 0; i < emoji.Count; i++)
                {
                    float x = (i % cols) * cell, y = (i / cols) * cell;
                    var r = new RectF { l = x, t = y, r = x + cell, b = y + cell };
                    string g = emoji[i].Glyph;
                    draw(rt, g, (uint)g.Length, fmt, ref r, brush, 4 /* ENABLE_COLOR_FONT */, 0);
                }
                ulong t1, t2;
                Check(Fn<EndDrawFn>(rt, 49)(rt, out t1, out t2), "EndDraw");

                var pixels = new byte[width * height * 4];
                Check(Fn<CopyPixelsFn>(bmp, 7)(bmp, IntPtr.Zero, (uint)(width * 4), (uint)pixels.Length, pixels), "CopyPixels");
                var atlas = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
                atlas.Freeze();
                for (int i = 0; i < emoji.Count; i++)
                {
                    var crop = new CroppedBitmap(atlas, new Int32Rect((i % cols) * cell, (i / cols) * cell, cell, cell));
                    crop.Freeze();
                    emoji[i].Image = crop;
                }
            }
            finally
            {
                Release(brush); Release(fmt); Release(rt); Release(bmp); Release(wic); Release(dwrite); Release(d2d);
            }
        }
    }
}
