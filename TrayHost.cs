using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>Ein Tray-Icon eines Programms (Discord, NordVPN, Steam …).</summary>
    internal sealed class TrayIconInfo
    {
        public IntPtr Hwnd { get; init; }
        public uint Id { get; init; }
        public Guid Guid { get; set; }
        public uint CallbackMessage { get; set; }
        public uint Version { get; set; }
        public string Tip { get; set; } = "";
        public ImageSource? Icon { get; set; }
        public bool Hidden { get; set; }

        public bool Is(IntPtr hwnd, uint id, Guid guid) =>
            (guid != Guid.Empty && Guid == guid) || (Hwnd == hwnd && Id == id);
    }

    /// <summary>
    /// Empfängt die Tray-Icons aller Programme selbst, damit Isle sie in der Top-Bar zeigen kann.
    ///
    /// So funktioniert es: Programme schicken ihre Icons an das Fenster mit dem Klassennamen „Shell_TrayWnd“
    /// (normal die Windows-Taskleiste). Isle legt ein unsichtbares Fenster mit diesem Namen an, das ganz oben
    /// liegt und daher zuerst gefunden wird, und bittet beim Start alle Programme, ihre Icons neu anzumelden.
    /// Jede Nachricht wird zusätzlich unverändert an die echte Taskleiste weitergereicht – die bleibt also
    /// vollständig, und nach dem Beenden von Isle ist alles wie vorher.
    /// (Gleiches Prinzip wie bei Taskleisten-Ersatzprogrammen wie RetroBar.)
    /// </summary>
    internal static class TrayHost
    {
        // ---------------------------------------------------------------- Windows-API
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        // So kommt ein Shell_NotifyIcon-Aufruf bei der Taskleiste an (Handles immer 32 Bit)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA32
        {
            public uint cbSize, hWnd, uID, uFlags, uCallbackMessage, hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public uint hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHELLTRAYDATA { public int dwSignature; public uint dwMessage; public NOTIFYICONDATA32 nid; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, IntPtr hInstance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h,
                                                    IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool SendNotifyMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);

        private const uint WM_COPYDATA = 0x004A, WM_USER = 0x0400;
        private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80;
        private const uint SMTO_ABORTIFHUNG = 0x2;
        private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

        private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_STATE = 0x8, NIF_GUID = 0x20;
        private const uint NIS_HIDDEN = 0x1;

        public const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
                          WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B, NIN_SELECT = 0x400;

        // ---------------------------------------------------------------- Zustand
        private static readonly WndProcDelegate Proc = WndProc; // Referenz halten, sonst räumt der GC sie weg
        private static IntPtr _hwnd;
        private static IntPtr _explorer;
        private static DispatcherTimer? _timer;
        private static readonly List<TrayIconInfo> _icons = new();

        public static readonly uint TaskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");

        /// <summary>Unsere eigene Rundmail „Taskleiste neu“ nicht als Explorer-Neustart missverstehen.</summary>
        public static DateTime IgnoreTaskbarCreatedUntil { get; private set; }

        public static bool Active => _hwnd != IntPtr.Zero;

        /// <summary>Sichtbare Icons (ohne versteckte, ohne die von beendeten Programmen).</summary>
        public static IReadOnlyList<TrayIconInfo> Icons =>
            _icons.Where(i => !i.Hidden && i.Icon != null && Win32.IsWindow(i.Hwnd)).ToList();

        public static event Action? Changed;

        // ---------------------------------------------------------------- Start / Stopp

        public static bool Start()
        {
            if (Active) return true;
            try
            {
                IntPtr hInst = Win32.GetModuleHandle(null);
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = hInst,
                    lpszClassName = "Shell_TrayWnd",
                };
                if (RegisterClassEx(ref wc) == 0) return false;

                _hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "Shell_TrayWnd", "", WS_POPUP,
                                       0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    UnregisterClass("Shell_TrayWnd", hInst);
                    return false;
                }

                KeepFirst();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _timer.Tick += (_, _) => KeepFirst();
                _timer.Start();

                // Alle Programme bitten, ihre Tray-Icons neu anzumelden – jetzt landen sie (auch) bei uns
                Broadcast();
                return true;
            }
            catch
            {
                Stop();
                return false;
            }
        }

        public static void Stop()
        {
            _timer?.Stop();
            _timer = null;
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                UnregisterClass("Shell_TrayWnd", Win32.GetModuleHandle(null));
                // Sicherheitshalber: alle Icons noch einmal bei der echten Taskleiste anmelden lassen
                Broadcast();
            }
            _icons.Clear();
        }

        private static int _bypass;

        /// <summary>Unser Fenster kurz ganz nach hinten, damit Windows die echte Taskleiste zuerst findet.</summary>
        public static void BeginBypass()
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_bypass++ == 0)
                Win32.SetWindowPos(_hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                                   Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        public static void EndBypass()
        {
            if (_hwnd == IntPtr.Zero) { _bypass = 0; return; }
            if (_bypass > 0 && --_bypass == 0) KeepFirst();
        }

        /// <summary>Für den Wächter: Isle ist weg → alle Programme bitten, ihre Icons bei Windows neu anzumelden.</summary>
        public static void AskAppsToReregister() => Broadcast();

        private static void Broadcast()
        {
            IgnoreTaskbarCreatedUntil = DateTime.Now.AddSeconds(4);
            SendNotifyMessage(HWND_BROADCAST, TaskbarCreatedMsg, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Unser Fenster muss vor der echten Taskleiste liegen, damit Programme es zuerst finden.
        /// Außerdem bekommt es deren Größe/Position – manche Programme richten Fenster daran aus.
        /// </summary>
        private static void KeepFirst()
        {
            if (_hwnd == IntPtr.Zero || _bypass > 0) return;
            IntPtr ex = ExplorerTray();
            uint flags = Win32.SWP_NOACTIVATE;
            int x = 0, y = 0, w = 0, h = 0;
            if (ex != IntPtr.Zero && Win32.GetWindowRect(ex, out var r))
            {
                x = r.Left; y = r.Top; w = r.Right - r.Left; h = r.Bottom - r.Top;
            }
            else flags |= Win32.SWP_NOMOVE | Win32.SWP_NOSIZE;
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, w, h, flags);
        }

        /// <summary>Die echte Windows-Taskleiste (gehört dem Explorer, nicht uns).</summary>
        public static IntPtr ExplorerTray()
        {
            if (_explorer != IntPtr.Zero && Win32.IsWindow(_explorer)) return _explorer;
            _explorer = IntPtr.Zero;
            IntPtr h = IntPtr.Zero;
            while ((h = Win32.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            {
                Win32.GetWindowThreadProcessId(h, out uint pid);
                if (pid != (uint)Environment.ProcessId) { _explorer = h; break; }
            }
            return _explorer;
        }

        // ---------------------------------------------------------------- Nachrichten

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == WM_COPYDATA && lParam != IntPtr.Zero)
                {
                    var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                    if (cds.dwData == new IntPtr(1) && cds.lpData != IntPtr.Zero)
                    {
                        // Tray-Icon: an die echte Taskleiste weiterreichen und selbst merken
                        Forward(msg, wParam, lParam);
                        HandleTray(cds);
                        return new IntPtr(1);
                    }
                    // Alles andere (z. B. Bildschirmrand-Anmeldungen von Programmen): unverändert durchreichen
                    return Forward(msg, wParam, lParam);
                }
                // Taskleisten-eigene Nachrichten (WM_USER … WM_APP) durchreichen. Registrierte Nachrichten ab 0xC000
                // nicht – die gehen meist als Rundmail an alle Fenster, der Explorer hat sie also schon selbst bekommen.
                if (msg >= WM_USER && msg < 0xC000)
                    return Forward(msg, wParam, lParam);
            }
            catch { }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private static IntPtr Forward(uint msg, IntPtr wParam, IntPtr lParam)
        {
            IntPtr ex = ExplorerTray();
            if (ex == IntPtr.Zero) return IntPtr.Zero;
            SendMessageTimeout(ex, msg, wParam, lParam, SMTO_ABORTIFHUNG, 3000, out IntPtr result);
            return result;
        }

        private static void HandleTray(COPYDATASTRUCT cds)
        {
            // Nur so viele Bytes lesen, wie geschickt wurden (alte Programme schicken kürzere Daten)
            int size = Marshal.SizeOf<SHELLTRAYDATA>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            SHELLTRAYDATA data;
            try
            {
                var zero = new byte[size];
                Marshal.Copy(zero, 0, buf, size);
                var bytes = new byte[Math.Min(size, Math.Max(0, cds.cbData))];
                Marshal.Copy(cds.lpData, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, buf, bytes.Length);
                data = Marshal.PtrToStructure<SHELLTRAYDATA>(buf);
            }
            finally { Marshal.FreeHGlobal(buf); }

            var nid = data.nid;
            var hwnd = new IntPtr((int)nid.hWnd);
            var guid = (nid.uFlags & NIF_GUID) != 0 ? nid.guidItem : Guid.Empty;
            var icon = _icons.FirstOrDefault(i => i.Is(hwnd, nid.uID, guid));

            switch (data.dwMessage)
            {
                case NIM_ADD:
                case NIM_MODIFY:
                    if (icon == null)
                    {
                        icon = new TrayIconInfo { Hwnd = hwnd, Id = nid.uID, Guid = guid };
                        _icons.Add(icon);
                    }
                    if ((nid.uFlags & NIF_MESSAGE) != 0) icon.CallbackMessage = nid.uCallbackMessage;
                    if ((nid.uFlags & NIF_TIP) != 0) icon.Tip = nid.szTip ?? "";
                    if ((nid.uFlags & NIF_ICON) != 0)
                    {
                        var img = FromHIcon(new IntPtr((int)nid.hIcon));
                        if (img != null) icon.Icon = img;
                    }
                    if ((nid.uFlags & NIF_STATE) != 0 && (nid.dwStateMask & NIS_HIDDEN) != 0)
                        icon.Hidden = (nid.dwState & NIS_HIDDEN) != 0;
                    break;

                case NIM_DELETE:
                    if (icon != null) _icons.Remove(icon);
                    break;

                case NIM_SETVERSION:
                    if (icon != null) icon.Version = nid.uVersion;
                    break;
            }

            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
        }

        private static ImageSource? FromHIcon(IntPtr hIcon)
        {
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- Klicks ans Programm schicken

        /// <param name="anchor">Mitte des Icons in Bildschirm-Pixeln (Programme öffnen dort ihr Menü)</param>
        public static void Send(TrayIconInfo icon, uint mouseMsg, Point anchor)
        {
            if (icon.CallbackMessage == 0 || !Win32.IsWindow(icon.Hwnd)) return;

            // Erlaubt dem Programm, sein Fenster/Menü nach vorn zu holen
            Win32.GetWindowThreadProcessId(icon.Hwnd, out uint pid);
            AllowSetForegroundWindow(pid);

            IntPtr w, l;
            if (icon.Version >= 4)
            {
                int x = (int)anchor.X, y = (int)anchor.Y;
                w = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
                l = new IntPtr((int)((icon.Id & 0xFFFF) << 16 | (mouseMsg & 0xFFFF)));
            }
            else
            {
                w = new IntPtr((int)icon.Id);
                l = new IntPtr((int)mouseMsg);
            }
            Win32.PostMessage(icon.Hwnd, icon.CallbackMessage, w, l);
        }

        public static void LeftClick(TrayIconInfo icon, Point anchor)
        {
            Send(icon, WM_LBUTTONDOWN, anchor);
            Send(icon, WM_LBUTTONUP, anchor);
            if (icon.Version >= 4) Send(icon, NIN_SELECT, anchor);
        }

        public static void DoubleClick(TrayIconInfo icon, Point anchor)
        {
            Send(icon, WM_LBUTTONDBLCLK, anchor);
            Send(icon, WM_LBUTTONUP, anchor);
        }

        public static void RightClick(TrayIconInfo icon, Point anchor)
        {
            Send(icon, WM_RBUTTONDOWN, anchor);
            Send(icon, WM_RBUTTONUP, anchor);
            if (icon.Version >= 4) Send(icon, WM_CONTEXTMENU, anchor);
        }
    }
}
