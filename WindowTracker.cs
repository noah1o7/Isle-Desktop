using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Isle
{
    /// <summary>Eine laufende App mit ihren sichtbaren Fenstern.</summary>
    public sealed class RunningApp
    {
        public string Key { get; init; } = "";
        public string? ExePath { get; init; }
        public string? AppId { get; init; }
        public string Name { get; init; } = "";
        public List<IntPtr> Windows { get; } = new();

        public string ExeName => ExePath != null ? Path.GetFileName(ExePath) : "";
    }

    internal static class WindowTracker
    {
        private static readonly int OwnPid = Environment.ProcessId;

        // Pro Fenster einmal ermitteln statt jede Sekunde neu (Prozess öffnen und COM sind teuer)
        private sealed record WinInfo(uint Pid, string? Exe, string? AppId);
        private static readonly Dictionary<IntPtr, WinInfo> InfoCache = new();
        private static readonly Dictionary<string, string> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
        };

        /// <summary>Diese Programme bekommen kein „Task beenden“ (würde Windows stören).</summary>
        private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", "applicationframehost.exe", "dwm.exe", "winlogon.exe", "csrss.exe",
            "svchost.exe", "sihost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        };

        /// <summary>Alle Apps mit Fenstern, die man auch in der Taskleiste sehen würde.</summary>
        public static List<RunningApp> Snapshot()
        {
            var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<IntPtr>();

            Win32.EnumWindows((h, _) =>
            {
                try
                {
                    if (!IsTaskbarWindow(h)) return true;
                    seen.Add(h);

                    if (!InfoCache.TryGetValue(h, out var info))
                    {
                        Win32.GetWindowThreadProcessId(h, out uint pid);
                        info = new WinInfo(pid, Win32.ProcessPath(pid), null);
                        // Nur Store-Apps und Apps mit eigener ID brauchen die (teure) App-ID
                        info = info with { AppId = Win32.WindowAppId(h) };
                        InfoCache[h] = info;
                    }
                    if (info.Pid == OwnPid) return true;

                    string exeName = info.Exe != null ? Path.GetFileName(info.Exe) : "";
                    string key = info.AppId ?? info.Exe ?? ("pid:" + info.Pid);
                    if (!apps.TryGetValue(key, out var app))
                    {
                        string name = exeName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
                            ? Win32.WindowTitle(h)
                            : Describe(info.Exe) ?? Win32.WindowTitle(h);
                        app = new RunningApp { Key = key, ExePath = info.Exe, AppId = info.AppId, Name = name };
                        apps[key] = app;
                    }
                    app.Windows.Add(h);
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            Current = seen;

            // Geschlossene Fenster aus dem Cache werfen
            if (InfoCache.Count > seen.Count + 32)
                foreach (var dead in InfoCache.Keys.Where(k => !seen.Contains(k)).ToList())
                    InfoCache.Remove(dead);

            return apps.Values.ToList();
        }

        /// <summary>Kennt das Dock dieses Fenster schon (aus der letzten Abfrage)?</summary>
        public static bool IsKnown(IntPtr h) => Current.Contains(h);

        /// <summary>Fenster, die bei der letzten Abfrage im Dock waren.</summary>
        private static HashSet<IntPtr> Current = new();

        public static bool IsTaskbarWindow(IntPtr h)
        {
            if (!Win32.IsWindowVisible(h)) return false;
            if (Win32.GetWindowTextLength(h) == 0) return false;
            if (Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) return false;
            long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64();
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return false;
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return false;
            if (Win32.IsCloaked(h)) return false;
            if (IgnoredClasses.Contains(Win32.WindowClass(h))) return false;
            return true;
        }

        /// <summary>Fenster, die noch existieren und sichtbar sind (für Vorschau und Menü).</summary>
        public static List<IntPtr> LiveWindows(RunningApp app) =>
            app.Windows.Where(h => Win32.IsWindow(h) && Win32.IsWindowVisible(h)).ToList();

        /// <summary>Lesbarer Programmname („Google Chrome“ statt „chrome.exe“).</summary>
        public static string? Describe(string? exe)
        {
            if (exe == null) return null;
            if (DescriptionCache.TryGetValue(exe, out var d)) return d;
            string result = Path.GetFileNameWithoutExtension(exe);
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) result = info.FileDescription!.Trim();
                else if (!string.IsNullOrWhiteSpace(info.ProductName)) result = info.ProductName!.Trim();
            }
            catch { }
            if (Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) result = "Datei-Explorer";
            DescriptionCache[exe] = result;
            return result;
        }

        /// <summary>Gehört diese laufende App zu diesem Dock-Pin?</summary>
        public static bool Matches(PinnedApp pin, RunningApp app)
        {
            if (app.AppId != null && app.AppId.Equals(pin.Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (pin.ExePath != null && app.ExePath != null && pin.ExePath.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
            if (app.ExePath != null && pin.Id.Equals(app.ExePath, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(pin.Name) && pin.Name.Equals(app.Name, StringComparison.CurrentCultureIgnoreCase)) return true;
            return false;
        }

        /// <summary>Klick im Dock: nach vorn holen, zwischen Fenstern wechseln oder minimieren.</summary>
        public static void Activate(RunningApp app)
        {
            var live = LiveWindows(app);
            if (live.Count == 0) return;

            IntPtr fg = Win32.GetForegroundWindow();
            int idx = live.IndexOf(fg);
            if (idx >= 0)
            {
                if (live.Count == 1) Win32.ShowWindow(fg, Win32.SW_MINIMIZE);
                else Win32.ForceForeground(live[(idx + 1) % live.Count]);
                return;
            }
            Win32.ForceForeground(live[0]);
        }

        public static void Close(IntPtr hwnd) => Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        public static void CloseAll(RunningApp app)
        {
            foreach (var h in LiveWindows(app)) Close(h);
        }

        public static bool CanKill(RunningApp app) =>
            app.ExePath != null && (!Protected.Contains(app.ExeName) || app.AppId != null && app.ExeName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase));

        /// <summary>„Task beenden“: beendet die Prozesse aller Fenster der App sofort, samt Unterprozessen.</summary>
        public static void Kill(RunningApp app)
        {
            var pids = new HashSet<int>();
            foreach (var h in LiveWindows(app))
            {
                int pid = RealPid(h);
                if (pid > 4 && pid != OwnPid) pids.Add(pid);
            }
            foreach (int pid in pids)
            {
                try
                {
                    string? exe = Win32.ProcessPath((uint)pid);
                    if (exe != null && Protected.Contains(Path.GetFileName(exe))) continue;
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        /// <summary>Prozess hinter einem Fenster; bei Store-Apps der echte App-Prozess statt ApplicationFrameHost.</summary>
        private static int RealPid(IntPtr hwnd)
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint framePid);
            string? exe = Win32.ProcessPath(framePid);
            if (exe == null || !Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                return (int)framePid;

            uint real = 0;
            Win32.EnumChildWindows(hwnd, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out uint p);
                if (p != framePid) { real = p; return false; }
                return true;
            }, IntPtr.Zero);
            return (int)(real != 0 ? real : framePid);
        }

        /// <summary>Name der App im Vordergrund (für die Top-Bar).</summary>
        public static string ForegroundAppName()
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return "";
            string cls = Win32.WindowClass(fg);
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return "Schreibtisch";
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == OwnPid) return "";
            string? exe = InfoCache.TryGetValue(fg, out var info) ? info.Exe : Win32.ProcessPath(pid);
            if (exe != null && Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
                return Win32.WindowTitle(fg);
            return Describe(exe) ?? "";
        }
    }
}
