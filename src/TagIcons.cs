using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ClipVault
{
    /// <summary>Icons for tags: a set of built-in Fluent glyphs, or a custom image drawn in the theme color.</summary>
    public static class TagIcons
    {
        /// <summary>Built-in choices (name, Segoe Fluent Icons code point), all in the app's outline style.</summary>
        public static readonly string[][] Defaults =
        {
            new[] { "Terminal", "E756" }, new[] { "Lock", "E72E" }, new[] { "Code", "E943" }, new[] { "Link", "E71B" },
            new[] { "Mail", "E715" }, new[] { "Key", "E8D7" }, new[] { "Web", "E774" }, new[] { "Note", "E70B" },
            new[] { "Star", "E734" }, new[] { "Work", "E821" }, new[] { "Bug", "EBE8" }, new[] { "Idea", "EA80" },
        };

        public static string GlyphSpec(string hex) { return "glyph:" + hex; }

        /// <summary>
        /// Builds the icon element. It takes its color from the control it sits in (tab, button), so it dims and
        /// brightens with that control exactly like the built-in icons. Returns null for an unusable spec.
        /// </summary>
        public static FrameworkElement Make(string spec, double size)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            if (spec.StartsWith("glyph:"))
            {
                int code;
                if (!int.TryParse(spec.Substring(6), System.Globalization.NumberStyles.HexNumber, null, out code)) return null;
                return new TextBlock
                {
                    Text = char.ConvertFromUtf32(code),
                    FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
                    FontSize = size,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            if (spec.StartsWith("file:"))
            {
                var img = Load(spec.Substring(5), (int)Math.Ceiling(size * 4));
                if (img == null) return null;
                // The picture is used as a mask and filled with the text color: a one-color silhouette like the other icons.
                var r = new Rectangle { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
                r.OpacityMask = new ImageBrush(img) { Stretch = Stretch.Uniform };
                r.SetBinding(Shape.FillProperty, new Binding("Foreground")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
                });
                return r;
            }
            return null;
        }

        static BitmapSource Load(string path, int px)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(File.ReadAllBytes(path));
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = px; // small and sharp; never keeps a huge picture in memory
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch (Exception ex) { Log.Write("Icon load failed: " + ex.Message); return null; }
        }

        /// <summary>Opens the icon picker under <paramref name="anchor"/>: built-in icons, Custom..., No icon.</summary>
        public static void ShowPicker(FrameworkElement anchor, Store store, string tag)
        {
            var res = Application.Current.Resources;
            string current = store.TagIcon(tag);
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };

            var grid = new UniformGrid { Columns = 6 };
            foreach (var d in Defaults)
            {
                string spec = GlyphSpec(d[1]);
                var b = new Button { Content = Make(spec, 16), Style = (Style)res["IconButton"], ToolTip = d[0] };
                if (spec == current) b.SetResourceReference(Control.BackgroundProperty, "HoverBrush"); // the chosen one
                b.Click += delegate { popup.IsOpen = false; store.SetTagIcon(tag, spec); };
                grid.Children.Add(b);
            }

            var custom = new Button { Content = "Custom…", Style = (Style)res["TextButton"], ToolTip = "Use an image file (a PNG with a transparent background works best)" };
            custom.Click += delegate
            {
                popup.IsOpen = false;
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose an icon for " + tag, Filter = "Images|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif" };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    if (Load(dlg.FileName, 32) == null) throw new InvalidDataException("This file isn't an image ClipVault can read.");
                    store.SetTagIcon(tag, store.ImportIcon(dlg.FileName));
                }
                catch (Exception ex) { Log.Write(ex); MessageBox.Show(ex.Message, "ClipVault", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            var none = new Button { Content = "No icon", Style = (Style)res["TextButton"], Margin = new Thickness(6, 0, 0, 0), IsEnabled = current != null };
            none.Click += delegate { popup.IsOpen = false; store.SetTagIcon(tag, null); };

            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            bottom.Children.Add(custom);
            bottom.Children.Add(none);

            var title = new TextBlock { Text = "Icon for " + tag, FontSize = 12, Margin = new Thickness(4, 0, 0, 6) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");

            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(grid);
            stack.Children.Add(bottom);

            var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 4, 8, 8), Child = stack };
            border.SetResourceReference(Border.BackgroundProperty, "MenuBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            border.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.25 };
            TextElement.SetFontFamily(border, (FontFamily)res["UiFont"]);
            border.SetResourceReference(TextElement.ForegroundProperty, "FgBrush");
            popup.Child = border;
            popup.IsOpen = true;
        }
    }
}
