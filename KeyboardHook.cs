using System;
using System.Runtime.InteropServices;
using System.Threading;
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
        private readonly Thread _thread;
        private uint _threadId;
        private volatile bool _enabled = true;
        private bool _winDown;
        private bool _comboUsed;
        // Tasten, die gerade gedrückt gehalten werden (z. B. W/A/S/D beim Laufen im Spiel)
        private readonly bool[] _held = new bool[256];

        public event Action? WinKeyAlone;
        public bool Enabled { get => _enabled; set => _enabled = value; }

        /// <summary>
        /// Der Hook läuft in einem eigenen, sonst leeren Thread. Lief er im Oberflächen-Thread und der war kurz
        /// beschäftigt (z. B. Startmenü lädt Icons), hat Windows den Hook stillschweigend entfernt –
        /// danach kam die Windows-Taste nie mehr bei Isle an.
        /// </summary>
        public KeyboardHook()
        {
            _proc = Callback;
            var ready = new ManualResetEventSlim(); // nicht disposen: der Thread setzt es evtl. erst nach dem Timeout
            _thread = new Thread(() =>
            {
                _threadId = Win32.GetCurrentThreadId();
                _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
                ready.Set();
                while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Win32.TranslateMessage(ref msg);
                    Win32.DispatchMessage(ref msg);
                }
                if (_hook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            })
            { IsBackground = true, Name = "Isle Tastatur" };
            _thread.Start();
            ready.Wait(3000);
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

                // Merken, welche Tasten gerade unten sind – auch wenn die Funktion aus ist
                bool wasHeld = false;
                if (!injected && k.vkCode < 256)
                {
                    wasHeld = _held[k.vkCode];
                    if (down) _held[k.vkCode] = true;
                    else if (up) _held[k.vkCode] = false;
                }

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
                        if (wasHeld)
                        {
                            // Taste wurde schon VOR der Windows-Taste gehalten (Laufen im Spiel) und meldet sich nur
                            // per Tastenwiederholung erneut. Das ist keine Kombi wie Win+D – und Windows würde daraus
                            // sonst z. B. Win+W (Widgets) machen. Die Wiederholung schlucken, das Spiel merkt nichts,
                            // weil die Taste für es ohnehin noch gedrückt ist.
                            return new IntPtr(1);
                        }
                        _comboUsed = true;
                    }
                }
            }
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
        }
    }
}
