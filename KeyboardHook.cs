using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace Isle
{
    /// <summary>
    /// Fängt das alleinige Drücken der Windows-Taste ab und öffnet stattdessen das Isle-Startmenü.
    /// Kombinationen wie Win+D, Win+E, Win+L laufen ganz normal weiter.
    /// </summary>
    internal sealed class KeyboardHook : IDisposable
    {
        private const byte VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_DUMMY = 0xFF;

        private readonly Win32.LowLevelKeyboardProc _proc; // Referenz halten, sonst räumt der GC sie weg
        private IntPtr _hook;
        private bool _winDown;
        private bool _comboUsed;

        public event Action? WinKeyAlone;
        public bool Enabled { get; set; } = true;

        public KeyboardHook()
        {
            _proc = Callback;
            _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                bool injected = (k.flags & Win32.LLKHF_INJECTED) != 0;
                int msg = wParam.ToInt32();
                bool down = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                bool up = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
                bool isWin = k.vkCode is VK_LWIN or VK_RWIN;

                if (!injected && Enabled)
                {
                    if (isWin && down)
                    {
                        if (!_winDown) { _winDown = true; _comboUsed = false; }
                    }
                    else if (isWin && up)
                    {
                        if (_winDown && !_comboUsed)
                        {
                            // Eine „leere“ Taste dazwischenschieben: Windows denkt dann, es war eine Kombi,
                            // und öffnet sein eigenes Startmenü nicht.
                            Win32.keybd_event(VK_DUMMY, 0, 0, UIntPtr.Zero);
                            Win32.keybd_event(VK_DUMMY, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                            Application.Current.Dispatcher.BeginInvoke(() => WinKeyAlone?.Invoke());
                        }
                        _winDown = false;
                    }
                    else if (down && _winDown)
                    {
                        _comboUsed = true;
                    }
                }
            }
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                Win32.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }
    }
}
