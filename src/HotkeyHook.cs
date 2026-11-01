using System;
using System.Runtime.InteropServices;

namespace ClipVault
{
    /// <summary>
    /// Catches Win+V with a low-level keyboard hook, so Windows' own clipboard panel never sees it.
    /// No system settings are changed: when ClipVault exits, Win+V goes back to the Windows panel.
    /// </summary>
    public class HotkeyHook : IDisposable
    {
        readonly Native.LowLevelKeyboardProc proc; // kept in a field so the GC never collects it
        IntPtr hook;
        bool swallowingV;

        /// <summary>Raised with the window that was in front when Win+V was pressed.</summary>
        public event Action<IntPtr> Pressed;

        public HotkeyHook()
        {
            proc = Callback;
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if (k.dwExtraInfo != Native.InjectedTag && k.vkCode == Native.VK_V)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                    if (down && (Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
                        && !Native.IsDown(Native.VK_CONTROL) && !Native.IsDown(Native.VK_MENU) && !Native.IsDown(Native.VK_SHIFT))
                    {
                        bool repeat = swallowingV;
                        swallowingV = true;
                        if (!repeat)
                        {
                            Native.TapDummyKey();
                            IntPtr fg = Native.GetForegroundWindow();
                            if (Pressed != null)
                                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => Pressed(fg)));
                        }
                        return new IntPtr(1);
                    }
                    if (up && swallowingV)
                    {
                        swallowingV = false;
                        return new IntPtr(1);
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }
}
