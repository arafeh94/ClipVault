using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipVault
{
    internal static class Native
    {
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_V = 0x56;
        public const int VK_DUMMY = 0xE8; // unassigned virtual key, used to stop the Start menu from opening
        public const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        public static readonly IntPtr InjectedTag = new IntPtr(0x434C4950); // "CLIP" - marks input we send ourselves

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public int type; public InputUnion u; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS { public int Left, Right, Top, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);

        /// <summary>Frees memory the app isn't using right now (it idles in the tray most of the time).</summary>
        public static void TrimMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }

        public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        public static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        static INPUT Key(ushort vk, ushort scan, uint flags)
        {
            INPUT i = new INPUT();
            i.type = 1;
            i.u.ki.wVk = vk;
            i.u.ki.wScan = scan;
            i.u.ki.dwFlags = flags;
            i.u.ki.dwExtraInfo = InjectedTag;
            return i;
        }

        static void Send(params INPUT[] inputs)
        {
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>Taps a key that does nothing, so releasing Win does not open the Start menu.</summary>
        public static void TapDummyKey()
        {
            Send(Key(VK_DUMMY, 0, 0), Key(VK_DUMMY, 0, KEYEVENTF_KEYUP));
        }

        public static void SendCtrlV()
        {
            var list = new System.Collections.Generic.List<INPUT>();
            foreach (int vk in new[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN })
                if (IsDown(vk)) list.Add(Key((ushort)vk, 0, KEYEVENTF_KEYUP));
            list.Add(Key(VK_CONTROL, 0, 0));
            list.Add(Key(VK_V, 0, 0));
            list.Add(Key(VK_V, 0, KEYEVENTF_KEYUP));
            list.Add(Key(VK_CONTROL, 0, KEYEVENTF_KEYUP));
            Send(list.ToArray());
        }

        public static void TypeText(string s)
        {
            var list = new System.Collections.Generic.List<INPUT>();
            foreach (int vk in new[] { VK_SHIFT, VK_CONTROL, VK_MENU })
                if (IsDown(vk)) list.Add(Key((ushort)vk, 0, KEYEVENTF_KEYUP));
            foreach (char c in s)
            {
                list.Add(Key(0, c, KEYEVENTF_UNICODE));
                list.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            Send(list.ToArray());
        }

        /// <summary>Brings a window to the foreground even when Windows would normally refuse.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
            uint pid;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        /// <summary>Screen position of the text caret in the foreground window, or the mouse position.</summary>
        public static POINT GetAnchor(IntPtr foreground, out bool fromCaret, out int caretHeight)
        {
            fromCaret = false;
            caretHeight = 0;
            uint pid;
            uint tid = GetWindowThreadProcessId(foreground, out pid);
            var info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            if (tid != 0 && GetGUIThreadInfo(tid, ref info) && info.hwndCaret != IntPtr.Zero)
            {
                var p = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
                if (ClientToScreen(info.hwndCaret, ref p) && (p.X != 0 || p.Y != 0))
                {
                    fromCaret = true;
                    caretHeight = Math.Max(16, info.rcCaret.Bottom - info.rcCaret.Top);
                    return p;
                }
            }
            POINT m;
            GetCursorPos(out m);
            return m;
        }

        public static RECT WorkAreaAt(POINT p)
        {
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(MonitorFromPoint(p, 2 /* nearest */), ref mi);
            return mi.rcWork;
        }

        /// <summary>Applies Windows 11 rounded corners, dark/light frame and the acrylic or Mica backdrop.</summary>
        public static void ApplyWindowEffects(IntPtr hwnd, bool dark, int backdrop)
        {
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref v, 4);           // DWMWA_USE_IMMERSIVE_DARK_MODE
            v = 2;
            DwmSetWindowAttribute(hwnd, 33, ref v, 4);           // DWMWA_WINDOW_CORNER_PREFERENCE = round
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            v = backdrop;
            DwmSetWindowAttribute(hwnd, 38, ref v, 4);           // DWMWA_SYSTEMBACKDROP_TYPE (2 = Mica, 3 = Acrylic)
        }

        public static string QuoteArg(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"', '\v' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
