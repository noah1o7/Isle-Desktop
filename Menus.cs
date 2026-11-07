using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace Isle
{
    /// <summary>
    /// Öffnet Menüs von Top-Bar und Dock so, dass sie sich wie gewohnt schließen:
    /// Klick irgendwo daneben (auch in anderen Programmen), nochmal Klick auf den Menü-Knopf,
    /// oder Wechsel zu einer anderen App.
    ///
    /// Hintergrund: Top-Bar und Dock nehmen absichtlich nie den Fokus. Dadurch bekommt das normale
    /// WPF-Menü Klicks in anderen Programmen nicht mit und blieb offen. Solange ein Menü offen ist,
    /// schaut deshalb ein kleiner Maus-Wächter auf Klicks – danach wird er sofort wieder entfernt.
    /// </summary>
    internal static class Menus
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public Win32.POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

        private static readonly Win32.LowLevelKeyboardProc Proc = MouseHook; // gleiche Signatur wie der Tastatur-Hook
        private static IntPtr _hook;
        private static object? _open;          // ContextMenu oder Popup
        private static Visual? _openVisual;    // Inhalt, über den wir das Fenster des Menüs finden
        private static Action? _close;
        private static FrameworkElement? _toggle;

        private static bool OpenNow => _open switch
        {
            ContextMenu m => m.IsOpen,
            Popup p => p.IsOpen,
            _ => false,
        };

        /// <summary>Ist gerade das Menü offen, das zu diesem Knopf gehört?</summary>
        public static bool IsOpenFor(FrameworkElement toggle) => OpenNow && _toggle == toggle;

        /// <param name="toggle">Knopf, der das Menü öffnet – ein Klick darauf schließt es wieder (statt neu zu öffnen).</param>
        public static void Open(ContextMenu menu, FrameworkElement target, PlacementMode placement,
                                double verticalOffset = 0, FrameworkElement? toggle = null)
        {
            Close();
            menu.StaysOpen = true; // Schließen regeln wir selbst
            menu.PlacementTarget = target;
            menu.Placement = placement;
            menu.VerticalOffset = verticalOffset;
            menu.Closed += (_, _) => Forget(menu);
            Remember(menu, menu, () => menu.IsOpen = false, toggle);
            menu.IsOpen = true;
            Hook();
        }

        /// <summary>Dasselbe für ein frei gestaltetes Popup (z. B. die Tray-Icons).</summary>
        public static void Open(Popup popup, FrameworkElement? toggle = null)
        {
            Close();
            popup.StaysOpen = true;
            popup.Closed -= PopupClosed;
            popup.Closed += PopupClosed;
            Remember(popup, popup.Child, () => popup.IsOpen = false, toggle);
            popup.IsOpen = true;
            Hook();
        }

        private static void PopupClosed(object? sender, EventArgs e) { if (sender != null) Forget(sender); }

        private static void Remember(object menu, Visual? visual, Action close, FrameworkElement? toggle)
        {
            _open = menu;
            _openVisual = visual;
            _close = close;
            _toggle = toggle;
        }

        private static void Forget(object menu)
        {
            if (_open != menu) return;
            _open = null;
            _openVisual = null;
            _close = null;
            _toggle = null;
            Unhook();
        }

        public static void Close() => _close?.Invoke();

        private static void Hook()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = Win32.SetWindowsHookEx(WH_MOUSE_LL, Proc, Win32.GetModuleHandle(null), 0);
        }

        private static void Unhook()
        {
            if (_hook == IntPtr.Zero) return;
            Win32.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        private static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _open != null)
            {
                int msg = wParam.ToInt32();
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    // Klick ins Menü selbst oder auf seinen Knopf (der schließt es dann per Klick) → nichts tun
                    if (!InsideMenu(info.pt) && !InsideToggle(info.pt))
                        Application.Current.Dispatcher.BeginInvoke(new Action(Close));
                }
            }
            return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool InsideMenu(Win32.POINT p)
        {
            if (_openVisual == null || PresentationSource.FromVisual(_openVisual) is not HwndSource src) return false;
            return Win32.GetWindowRect(src.Handle, out var r) && Contains(r, p);
        }

        private static bool InsideToggle(Win32.POINT p)
        {
            var t = _toggle;
            if (t == null || PresentationSource.FromVisual(t) == null) return false;
            var a = t.PointToScreen(new Point(0, 0));
            var b = t.PointToScreen(new Point(t.ActualWidth, t.ActualHeight));
            return p.X >= a.X && p.X < b.X && p.Y >= a.Y && p.Y < b.Y;
        }

        private static bool Contains(Win32.RECT r, Win32.POINT p) =>
            p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }
}
