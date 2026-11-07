using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>
    /// Lässt sich von Windows melden, wenn Fenster auf-/zugehen oder die aktive App wechselt.
    /// Dadurch reagieren Dock und Top-Bar sofort, statt ständig alles abzufragen.
    /// </summary>
    internal sealed class WinEvents : IDisposable
    {
        public static WinEvents? Instance { get; private set; }
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C; // neue Fenster bekommen ihren Titel oft erst nach dem Anzeigen

        private readonly Win32.WinEventProc _proc; // Referenz halten, sonst räumt der GC sie weg
        private readonly List<IntPtr> _hooks = new();
        private readonly DispatcherTimer _debounce;

        /// <summary>Aktive App hat gewechselt (sofort).</summary>
        public event Action? ForegroundChanged;
        /// <summary>Fenster wurde geöffnet, geschlossen, minimiert … (gebündelt, max. ~6× pro Sekunde).</summary>
        public event Action? WindowsChanged;

        public WinEvents()
        {
            Instance = this;
            _proc = OnEvent;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                WindowsChanged?.Invoke();
            };

            uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
            Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, flags);
            Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_HIDE, flags);
            Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, flags);
            Hook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED, flags);
        }

        private void Hook(uint min, uint max, uint flags)
        {
            IntPtr h = Win32.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, flags);
            if (h != IntPtr.Zero) _hooks.Add(h);
        }

        private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // nur ganze Fenster

            if (evt == Win32.EVENT_SYSTEM_FOREGROUND)
            {
                ForegroundChanged?.Invoke();
                Kick();
                return;
            }

            // Windows hat die (versteckte) Taskleiste wieder eingeblendet → sofort wieder weg
            if (evt == Win32.EVENT_OBJECT_SHOW && Win32.GetAncestor(hwnd, Win32.GA_ROOT) == hwnd &&
                TaskbarControl.IsTaskbarClass(Win32.WindowClass(hwnd)))
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(TaskbarControl.EnsureHidden);
                return;
            }

            // Windows meldet auch Tooltips, Menüs, Kind-Fenster usw. – nur echte App-Fenster interessieren:
            // Verschwinden zählt nur bei Fenstern, die das Dock kennt; Erscheinen nur bei Taskleisten-Fenstern.
            bool relevant = evt switch
            {
                Win32.EVENT_OBJECT_DESTROY or Win32.EVENT_OBJECT_HIDE or Win32.EVENT_OBJECT_CLOAKED
                    => WindowTracker.IsKnown(hwnd),
                Win32.EVENT_OBJECT_SHOW or Win32.EVENT_OBJECT_UNCLOAKED or EVENT_OBJECT_NAMECHANGE
                    => !WindowTracker.IsKnown(hwnd) && Win32.GetAncestor(hwnd, Win32.GA_ROOT) == hwnd
                       && WindowTracker.IsTaskbarWindow(hwnd),
                _ => false,
            };
            if (relevant) Kick();
        }

        private void Kick()
        {
            if (!_debounce.IsEnabled) _debounce.Start();
        }

        public void Dispose()
        {
            foreach (var h in _hooks) Win32.UnhookWinEvent(h);
            _hooks.Clear();
            _debounce.Stop();
        }
    }
}
