using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace ClipVault
{
    /// <summary>
    /// Catches Win+V with a low-level keyboard hook, so Windows' own clipboard panel never sees it.
    /// No system settings are changed: when ClipVault exits, Win+V goes back to the Windows panel.
    ///
    /// Windows passes every keystroke on the PC through this hook before any app gets it, so the hook
    /// lives on its own thread that does nothing else. Work on ClipVault's UI thread (pasting a large
    /// image, building the list...) can then never delay typing in other apps.
    /// </summary>
    public class HotkeyHook : IDisposable
    {
        Native.LowLevelKeyboardProc proc; // kept in a field so the GC never collects it
        IntPtr hook;
        bool swallowingV;
        uint threadId;
        readonly Dispatcher ui;
        readonly Thread thread;

        /// <summary>Raised on the UI thread with the window that was in front when Win+V was pressed.</summary>
        public event Action<IntPtr> Pressed;

        public HotkeyHook()
        {
            ui = Dispatcher.CurrentDispatcher;
            var ready = new ManualResetEventSlim();
            thread = new Thread(() =>
            {
                threadId = Native.GetCurrentThreadId();
                proc = Callback;
                hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
                ready.Set();
                // A low-level hook is called through this thread's message loop.
                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            }) { IsBackground = true, Name = "ClipVault keyboard hook", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait(5000);
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
                            var handler = Pressed;
                            if (handler != null) ui.BeginInvoke(new Action(() => handler(fg)));
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
            if (threadId != 0) Native.PostThreadMessage(threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            threadId = 0;
        }
    }
}
