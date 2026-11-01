using System;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace ClipVault
{
    /// <summary>Where to open the popup: under the text cursor when it can be found, like Windows' own Win+V.</summary>
    internal struct Anchor
    {
        public Native.POINT Point;   // top-left of the caret (or of the text box), in physical screen pixels
        public int LineHeight;        // height of the caret line; 0 when anchored to the mouse
        public bool FromText;         // true when anchored to a caret or text box, false for the mouse
        public string Source;         // for the log: "win32-caret", "uia-caret", "uia-focus", "mouse"
    }

    internal static class CaretLocator
    {
        const int UiaTimeoutMs = 250;

        /// <summary>
        /// 1. Classic Win32 caret (instant; Notepad, Explorer, most Win32 apps).
        /// 2. UI Automation text cursor (what Windows uses; Chrome, Edge, VS Code, Terminal, Office, WinUI...).
        /// 3. UI Automation focused text box, when it's small enough to be a single field.
        /// 4. Mouse pointer.
        /// Steps 2-3 run on a background thread with a time limit, so Win+V never feels slow.
        /// </summary>
        public static async Task<Anchor> Find(IntPtr foreground)
        {
            bool fromCaret;
            int caretH;
            var p = Native.GetAnchor(foreground, out fromCaret, out caretH);
            if (fromCaret) return new Anchor { Point = p, LineHeight = caretH, FromText = true, Source = "win32-caret" };

            var uia = Task.Run(() => FromAutomation());
            if (await Task.WhenAny(uia, Task.Delay(UiaTimeoutMs)) == uia && uia.Result.HasValue) return uia.Result.Value;

            Native.POINT m;
            Native.GetCursorPos(out m);
            return new Anchor { Point = m, LineHeight = 0, FromText = false, Source = "mouse" };
        }

        static Anchor? FromAutomation()
        {
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el == null) return null;

                object pattern;
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    var tp = (TextPattern)pattern;
                    var sel = tp.GetSelection();
                    if (sel != null && sel.Length > 0)
                    {
                        var range = sel[0];
                        var rects = range.GetBoundingRectangles();
                        if (rects == null || rects.Length == 0)
                        {
                            // An empty selection (just a caret) has no size; measure the character next to it.
                            var probe = range.Clone();
                            probe.ExpandToEnclosingUnit(TextUnit.Character);
                            rects = probe.GetBoundingRectangles();
                        }
                        if (rects != null && rects.Length > 0 && !rects[0].IsEmpty && rects[0].Width < 4000)
                        {
                            var r = rects[0];
                            return new Anchor
                            {
                                Point = new Native.POINT { X = (int)r.Left, Y = (int)r.Top },
                                LineHeight = Math.Max(16, (int)r.Height),
                                FromText = true,
                                Source = "uia-caret"
                            };
                        }
                    }
                }

                // No caret available: use the focused field itself if it looks like a single text box.
                var box = el.Current.BoundingRectangle;
                bool editable = el.Current.ControlType == ControlType.Edit || el.Current.ControlType == ControlType.ComboBox || el.Current.ControlType == ControlType.Document;
                if (editable && !box.IsEmpty && box.Height > 0 && box.Height <= 120)
                {
                    return new Anchor
                    {
                        Point = new Native.POINT { X = (int)box.Left, Y = (int)box.Top },
                        LineHeight = (int)box.Height,
                        FromText = true,
                        Source = "uia-focus"
                    };
                }
            }
            catch (Exception ex) { Log.Write("UI Automation caret lookup failed: " + ex.Message); }
            return null;
        }
    }
}
