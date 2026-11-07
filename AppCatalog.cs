using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace Isle
{
    public sealed class AppEntry
    {
        public string Name { get; init; } = "";
        /// <summary>Kennung im Windows-App-Ordner (shell:AppsFolder\…).</summary>
        public string Id { get; init; } = "";
        public string? ExePath { get; init; }

        public string IconKey => @"shell:AppsFolder\" + Id;
    }

    /// <summary>Alle installierten Apps – dieselbe Liste wie „Alle Apps“ im Windows-Startmenü (inkl. Store-Apps).</summary>
    internal static class AppCatalog
    {
        public static IReadOnlyList<AppEntry> Apps { get; private set; } = Array.Empty<AppEntry>();
        public static event Action? Loaded;

        private static readonly string[] JunkWords = { "uninstall", "deinstall", "readme", "release notes", "website" };
        private static readonly string[] JunkExt = { ".url", ".chm", ".txt", ".pdf", ".html", ".htm", ".ini", ".log" };

        public static void Refresh()
        {
            var dispatcher = Application.Current.Dispatcher;
            StaWorker.Run(() =>
            {
                var list = new List<AppEntry>();
                try
                {
                    var shellType = Type.GetTypeFromProgID("Shell.Application");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType)!;
                        dynamic folder = shell.NameSpace("shell:AppsFolder");
                        dynamic items = folder.Items();
                        int count = items.Count;
                        for (int i = 0; i < count; i++)
                        {
                            dynamic item = items.Item(i);
                            if (item == null) continue;
                            string name = item.Name ?? "";
                            string id = item.Path ?? "";
                            if (name.Length == 0 || id.Length == 0) continue;
                            string lower = name.ToLowerInvariant();
                            if (JunkWords.Any(lower.Contains)) continue;
                            if (JunkExt.Any(e => id.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                            list.Add(new AppEntry { Name = name, Id = id, ExePath = ResolveExePath(id) });
                        }
                    }
                }
                catch { }

                var sorted = list
                    .GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                dispatcher.BeginInvoke(() =>
                {
                    Apps = sorted;
                    Loaded?.Invoke();
                });
            });
        }

        private static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,
            ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.SpecialFolder.SystemX86,
        };

        /// <summary>Versucht, aus der App-Kennung den echten Exe-Pfad zu machen (für „läuft gerade?“).</summary>
        public static string? ResolveExePath(string id)
        {
            try
            {
                if (id.Equals("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

                string path = id;
                if (path.StartsWith("{"))
                {
                    int end = path.IndexOf('}');
                    if (end < 0) return null;
                    string guid = path[..(end + 1)];
                    if (!KnownFolders.TryGetValue(guid, out var sf)) return null;
                    path = Path.Combine(Environment.GetFolderPath(sf), path[(end + 1)..].TrimStart('\\'));
                }
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(path) && File.Exists(path))
                    return path;
            }
            catch { }
            return null;
        }

        public static AppEntry? FindById(string id) =>
            Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public static AppEntry? FindByName(params string[] names)
        {
            foreach (var n in names)
            {
                var hit = Apps.FirstOrDefault(a => a.Name.Equals(n, StringComparison.CurrentCultureIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        public static void Launch(string id, string? exePath)
        {
            try
            {
                // Aufs Dock gezogene Datei/Verknüpfung/Ordner: einfach öffnen
                if (Path.IsPathRooted(id) && !id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(id) || Directory.Exists(id)))
                {
                    Process.Start(new ProcessStartInfo(id) { UseShellExecute = true });
                    return;
                }
                if (exePath != null && File.Exists(exePath) && !id.Contains('!'))
                    Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + id) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>Ziel einer .lnk-Verknüpfung (nur wenn es ein Programm ist).</summary>
        public static string? ShortcutTarget(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                dynamic shell = Activator.CreateInstance(t)!;
                string target = shell.CreateShortcut(lnk).TargetPath;
                return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target) ? target : null;
            }
            catch { return null; }
        }

        public static void Open(string pathOrShellUri)
        {
            try { Process.Start(new ProcessStartInfo(pathOrShellUri) { UseShellExecute = true }); }
            catch
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", pathOrShellUri) { UseShellExecute = true }); } catch { }
            }
        }
    }
}
