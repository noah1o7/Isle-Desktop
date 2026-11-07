using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>
    /// Meldet ein Fenster bei Windows als „AppBar“ an (wie die Taskleiste): Der Rand wird reserviert,
    /// maximierte Fenster gehen nicht mehr darunter.
    /// </summary>
    internal sealed class AppBar : IDisposable
    {
        private readonly Window _window;
        private readonly uint _edge;
        private readonly double _reserveDip;
        private readonly double _windowHeightDip;
        private IntPtr _hwnd;
        private uint _callbackMsg;
        private bool _registered;
        private Win32.RECT _last;
        private int _lastHeight;
        private bool _hasLast;

        public event Action<bool>? FullscreenChanged;

        /// <summary>Alle angemeldeten Ränder (Top-Bar, Dock) – für den Rand-Wächter.</summary>
        public static readonly List<AppBar> All = new();
        public uint Edge => _edge;
        public Win32.RECT? Reserved => _registered && _hasLast ? _last : null;

        /// <param name="reserveDip">So viel Platz wird für andere Fenster gesperrt.</param>
        /// <param name="windowHeightDip">So hoch ist das Fenster selbst (darf größer sein, z. B. für die Dock-Vergrößerung).</param>
        public AppBar(Window window, uint edge, double reserveDip, double windowHeightDip)
        {
            _window = window;
            _edge = edge;
            _reserveDip = reserveDip;
            _windowHeightDip = windowHeightDip;
        }

        public void Register()
        {
            if (_registered) return;
            _hwnd = new WindowInteropHelper(_window).Handle;
            _callbackMsg = Win32.RegisterWindowMessage("Isle.AppBar.Callback");
            var abd = NewData();
            abd.uCallbackMessage = _callbackMsg;
            Win32.AppBarMsg(Win32.ABM_NEW, ref abd);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            _window.DpiChanged += OnDpiChanged;
            _registered = true;
            if (!All.Contains(this)) All.Add(this);
            UpdatePosition();
        }

        public void Unregister()
        {
            if (!_registered) return;
            var abd = NewData();
            Win32.AppBarMsg(Win32.ABM_REMOVE, ref abd);
            HwndSource.FromHwnd(_hwnd)?.RemoveHook(WndProc);
            _window.DpiChanged -= OnDpiChanged;
            _registered = false;
            All.Remove(this);
            _hasLast = false;
            _placedX = int.MinValue;
        }

        private void OnDpiChanged(object sender, DpiChangedEventArgs e) => UpdatePosition();

        private Win32.APPBARDATA NewData() => new()
        {
            cbSize = Marshal.SizeOf<Win32.APPBARDATA>(),
            hWnd = _hwnd,
            uEdge = _edge,
        };

        public void UpdatePosition()
        {
            if (!_registered) return;
            double scale = VisualTreeHelper.GetDpi(_window).DpiScaleY;
            int reservePx = (int)Math.Round(_reserveDip * scale);
            int heightPx = (int)Math.Round(_windowHeightDip * scale);
            var mon = Win32.PrimaryMonitorRect();

            var abd = NewData();
            abd.rc = mon;
            if (_edge == Win32.ABE_TOP) abd.rc.Bottom = abd.rc.Top + reservePx;
            else abd.rc.Top = abd.rc.Bottom - reservePx;

            Win32.AppBarMsg(Win32.ABM_QUERYPOS, ref abd);
            if (_edge == Win32.ABE_TOP) abd.rc.Bottom = abd.rc.Top + reservePx;
            else abd.rc.Top = abd.rc.Bottom - reservePx;

            // Nur melden, wenn sich wirklich etwas geändert hat – sonst schicken sich
            // Top-Bar und Dock gegenseitig endlos „Position geändert“-Nachrichten.
            bool same = _hasLast && abd.rc.Left == _last.Left && abd.rc.Top == _last.Top
                        && abd.rc.Right == _last.Right && abd.rc.Bottom == _last.Bottom && heightPx == _lastHeight;
            if (!same)
            {
                _last = abd.rc;
                _lastHeight = heightPx;
                _hasLast = true;
                Win32.AppBarMsg(Win32.ABM_SETPOS, ref abd);
            }
            PlaceWindow();
        }

        /// <summary>
        /// Breite des Fensters selbst (null = ganze Bildschirmbreite). Ein schmaleres transparentes Fenster
        /// muss Windows bei jeder Animation viel weniger neu zeichnen → flüssiger.
        /// </summary>
        public double? WindowWidthDip { get; set; }

        private int _placedX = int.MinValue, _placedY, _placedW, _placedH;

        public void PlaceWindow()
        {
            if (!_registered || !_hasLast) return;
            var rc = _last;
            double scale = VisualTreeHelper.GetDpi(_window).DpiScaleX;
            int fullW = rc.Right - rc.Left;
            int w = WindowWidthDip is double dip ? Math.Min(fullW, (int)Math.Round(dip * scale)) : fullW;
            int x = rc.Left + (fullW - w) / 2;
            int y = _edge == Win32.ABE_TOP ? rc.Top : rc.Bottom - _lastHeight;
            if (x == _placedX && y == _placedY && w == _placedW && _lastHeight == _placedH) return;
            (_placedX, _placedY, _placedW, _placedH) = (x, y, w, _lastHeight);
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, _lastHeight, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_registered && msg == (int)_callbackMsg)
            {
                switch (wParam.ToInt32())
                {
                    case Win32.ABN_POSCHANGED:
                        UpdatePosition();
                        break;
                    case Win32.ABN_FULLSCREENAPP:
                        FullscreenChanged?.Invoke(lParam != IntPtr.Zero);
                        break;
                }
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose() => Unregister();
    }

    /// <summary>
    /// Sorgt dafür, dass maximierte Fenster unter der Top-Bar anfangen und über dem Dock aufhören.
    /// Normalerweise erledigt das Windows über die Rand-Anmeldung. Falls Windows den Bereich trotzdem
    /// zurücksetzt, stellt Isle ihn selbst richtig ein (gilt nur, solange Isle läuft – Windows rechnet ihn
    /// beim Beenden neu aus).
    /// </summary>
    internal static class WorkAreaGuard
    {
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, ref Win32.RECT rect, uint winIni);
        private const uint SPI_SETWORKAREA = 0x2F, SPI_GETWORKAREA = 0x30, SPIF_SENDCHANGE = 0x2;
        private static DateTime _lastSet = DateTime.MinValue;

        public static void Ensure()
        {
            // Ist die Windows-Taskleiste normal sichtbar, rechnet Windows selbst (inkl. Taskleiste) – nicht eingreifen
            if (!Settings.Current.AutoHideTaskbar) return;
            try
            {
                var want = Win32.PrimaryMonitorRect();
                bool any = false;
                foreach (var bar in AppBar.All)
                {
                    if (bar.Reserved is not Win32.RECT r) continue;
                    any = true;
                    if (bar.Edge == Win32.ABE_TOP) want.Top = Math.Max(want.Top, r.Bottom);
                    else want.Bottom = Math.Min(want.Bottom, r.Top);
                }
                if (!any || want.Bottom <= want.Top) return;

                var cur = new Win32.RECT();
                if (!SystemParametersInfo(SPI_GETWORKAREA, 0, ref cur, 0)) return;
                if (cur.Left == want.Left && cur.Top == want.Top && cur.Right == want.Right && cur.Bottom == want.Bottom) return;

                // Nicht im Sekundentakt mit Windows hin und her stellen
                if ((DateTime.Now - _lastSet).TotalSeconds < 1.5) return;
                _lastSet = DateTime.Now;

                // SENDCHANGE: maximierte Fenster passen sich sofort an
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref want, SPIF_SENDCHANGE);
            }
            catch { }
        }
    }

    /// <summary>
    /// Versteckt die Windows-Taskleiste komplett, solange Isle läuft (auf allen Monitoren),
    /// und stellt sie beim Beenden wieder her. Zusätzlich steht sie auf „automatisch ausblenden“,
    /// damit Windows unten keinen Platz mehr für sie freihält.
    /// </summary>
    internal static class TaskbarControl
    {
        private static bool _hidden;
        private static bool _peeking;
        private static DateTime _peekStart;
        private static int _peekMiss;
        private static DispatcherTimer? _peekTimer;

        public static bool IsPeeking => _peeking;

        private static Win32.APPBARDATA Data() => new()
        {
            cbSize = Marshal.SizeOf<Win32.APPBARDATA>(),
            hWnd = TrayHost.ExplorerTray(),
        };

        private static int GetState()
        {
            var d = Data();
            return (int)Win32.AppBarMsg(Win32.ABM_GETSTATE, ref d).ToUInt32();
        }

        private static void SetState(int state)
        {
            var d = Data();
            d.lParam = new IntPtr(state);
            Win32.AppBarMsg(Win32.ABM_SETSTATE, ref d);
        }

        /// <summary>Haupt-Taskleiste und die Taskleisten weiterer Monitore.</summary>
        private static List<IntPtr> TaskbarWindows()
        {
            var list = new List<IntPtr>();
            IntPtr main = TrayHost.ExplorerTray();
            if (main != IntPtr.Zero) list.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = Win32.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                list.Add(h);
            return list;
        }

        public static bool IsTaskbarClass(string cls) => cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

        public static void Apply(bool hide)
        {
            try
            {
                if (!hide)
                {
                    Restore();
                    return;
                }
                var s = Settings.Current;
                int now = GetState();
                if (s.OriginalTaskbarState == null)
                {
                    s.OriginalTaskbarState = now;
                    Settings.Save();
                }
                if ((now & Win32.ABS_AUTOHIDE) == 0) SetState(now | Win32.ABS_AUTOHIDE);
                _hidden = true;
                EnsureHidden();
            }
            catch { }
        }

        /// <summary>Wird aufgerufen, wenn Windows die Taskleiste wieder zeigt → sofort wieder weg.</summary>
        public static void EnsureHidden()
        {
            if (!_hidden || _peeking) return;
            foreach (var w in TaskbarWindows())
                if (Win32.IsWindowVisible(w)) Win32.ShowWindow(w, Win32.SW_HIDE);
        }

        public static void Restore()
        {
            _hidden = false;
            _peeking = false;
            _peekTimer?.Stop();
            try
            {
                foreach (var w in TaskbarWindows()) Win32.ShowWindow(w, Win32.SW_SHOWNA);
                var s = Settings.Current;
                if (s.OriginalTaskbarState is int original)
                {
                    SetState(original);
                    s.OriginalTaskbarState = null;
                    Settings.Save();
                }
            }
            catch { }
        }

        /// <summary>
        /// Notfall-Wiederherstellung aus einem anderen Prozess (Wächter), falls Isle abstürzt
        /// oder hart beendet wird: Taskleiste zeigen und alten Zustand zurück.
        /// </summary>
        public static void EmergencyRestore()
        {
            Settings.Load();
            Restore();
        }

        /// <summary>
        /// Taskleiste kurz einblenden, z. B. für Tray-Icons. Sobald sie wieder eingefahren ist
        /// (Maus weg), wird sie erneut versteckt.
        /// </summary>
        public static void Peek()
        {
            if (_hidden)
            {
                _peeking = true;
                foreach (var w in TaskbarWindows()) Win32.ShowWindow(w, Win32.SW_SHOWNA);
            }
            // Win+B setzt den Fokus auf den Infobereich → die Taskleiste fährt hoch
            Win32.SendCombo(0x5B /* Win */, 0x42 /* B */);
            if (!_hidden) return;

            _peekStart = DateTime.Now;
            _peekMiss = 0;
            if (_peekTimer == null)
            {
                _peekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _peekTimer.Tick += (_, _) => PeekTick();
            }
            _peekTimer.Start();
        }

        private static void PeekTick()
        {
            if (!_peeking) { _peekTimer?.Stop(); return; }
            if ((DateTime.Now - _peekStart).TotalSeconds < 1.5) return;

            // Noch in Benutzung: Taskleiste hochgefahren oder ein Tray-Menü/Flyout ist vorne
            if (IsRevealed() || ForegroundIsShell()) { _peekMiss = 0; return; }
            if (++_peekMiss < 3) return;

            _peeking = false;
            _peekTimer?.Stop();
            EnsureHidden();
        }

        private static bool ForegroundIsShell()
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string cls = Win32.WindowClass(fg);
            if (IsTaskbarClass(cls) || cls is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
                or "Xaml_WindowedPopupClass" or "#32768") return true;
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            string? exe = Win32.ProcessPath(pid);
            return exe != null && System.IO.Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
                   && cls is not ("CabinetWClass" or "Progman" or "WorkerW");
        }

        /// <summary>Ist die (automatisch ausgeblendete) Taskleiste gerade hochgefahren?</summary>
        public static bool IsRevealed()
        {
            IntPtr tb = TrayHost.ExplorerTray();
            if (tb == IntPtr.Zero || !Win32.IsWindowVisible(tb) || !Win32.GetWindowRect(tb, out var r)) return false;
            var mon = Win32.PrimaryMonitorRect();
            return r.Top < mon.Bottom - 8 && r.Bottom > mon.Top + 8 && (r.Bottom - r.Top) < (mon.Bottom - mon.Top) / 2;
        }
    }
}
