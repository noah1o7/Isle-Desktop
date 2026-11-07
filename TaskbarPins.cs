using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Isle
{
    /// <summary>
    /// Liest die an die Windows-Taskleiste angehefteten Apps – in derselben Reihenfolge wie dort.
    /// Normale Programme liegen als Verknüpfung im Ordner „User Pinned\TaskBar“,
    /// Store-Apps (Terminal, Rechner …) nur in der Registry (Taskband\Favorites).
    /// </summary>
    internal static class TaskbarPins
    {
        private static readonly string PinFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

        public static List<PinnedApp> Read()
        {
            var lnkFiles = Directory.Exists(PinFolder)
                ? Directory.GetFiles(PinFolder, "*.lnk").ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var storeApps = AppCatalog.Apps.Where(a => a.Id.Contains('!')).ToList();

            var result = new List<PinnedApp>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) Reihenfolge aus der Registry: dort stehen die Verknüpfungsnamen und Store-App-Kennungen
            var candidates = lnkFiles.Keys.Concat(storeApps.Select(a => a.Id));
            foreach (var token in OrderInRegistry(candidates))
            {
                if (!used.Add(token)) continue;
                if (lnkFiles.TryGetValue(token, out var file)) result.Add(FromShortcut(file));
                else if (storeApps.FirstOrDefault(a => a.Id.Equals(token, StringComparison.OrdinalIgnoreCase)) is { } app)
                    result.Add(new PinnedApp { Id = app.Id, Name = app.Name, ExePath = app.ExePath });
            }

            // 2) Verknüpfungen, die in der Registry nicht auftauchten, hinten anhängen
            foreach (var (name, file) in lnkFiles.OrderBy(kv => File.GetCreationTime(kv.Value)))
                if (used.Add(name)) result.Add(FromShortcut(file));

            return result;
        }

        private static PinnedApp FromShortcut(string lnk) => new()
        {
            Id = lnk,
            Name = Path.GetFileNameWithoutExtension(lnk),
            ExePath = AppCatalog.ShortcutTarget(lnk),
        };

        /// <summary>
        /// Sucht die Kandidaten im Registry-Eintrag „Favorites“ und gibt die gefundenen in dessen Reihenfolge zurück.
        /// Der Eintrag ist binär; die Namen stehen darin als UTF-16-Text (an gerader oder ungerader Position).
        /// </summary>
        private static IEnumerable<string> OrderInRegistry(IEnumerable<string> candidates)
        {
            byte[]? blob = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
                blob = key?.GetValue("Favorites") as byte[];
            }
            catch { }
            if (blob == null || blob.Length < 4) return Array.Empty<string>();

            var texts = new[]
            {
                Encoding.Unicode.GetString(blob, 0, blob.Length & ~1),
                Encoding.Unicode.GetString(blob, 1, (blob.Length - 1) & ~1),
            };

            var found = new List<(int offset, string token)>();
            foreach (var c in candidates)
            {
                for (int align = 0; align < 2; align++)
                {
                    int idx = texts[align].IndexOf(c, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) { found.Add((align + idx * 2, c)); break; }
                }
            }
            return found.OrderBy(f => f.offset).Select(f => f.token);
        }

        /// <summary>Gehört dieser Taskleisten-Eintrag schon zum Dock?</summary>
        public static bool AlreadyPinned(PinnedApp candidate, IEnumerable<PinnedApp> pins) =>
            pins.Any(p =>
                p.Id.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase) ||
                (p.ExePath != null && candidate.ExePath != null && p.ExePath.Equals(candidate.ExePath, StringComparison.OrdinalIgnoreCase)) ||
                p.Name.Equals(candidate.Name, StringComparison.CurrentCultureIgnoreCase));
    }
}
