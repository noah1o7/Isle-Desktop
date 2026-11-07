using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Isle
{
    public sealed class PinnedApp
    {
        /// <summary>Kennung im Windows-App-Ordner (AppUserModelID oder Pfad).</summary>
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ExePath { get; set; }
    }

    public sealed class ReminderItem
    {
        public string Text { get; set; } = "";
        public DateTime? Due { get; set; }
        public bool Notified { get; set; }
    }

    /// <summary>Alle Einstellungen, gespeichert unter %AppData%\Isle\settings.json.</summary>
    public sealed class Settings
    {
        public bool ShowDock { get; set; } = true;
        public bool AutoHideTaskbar { get; set; } = true;
        public bool WinKeyOpensStart { get; set; } = true;
        public string? WeatherCity { get; set; }
        public double? WeatherLat { get; set; }
        public double? WeatherLon { get; set; }
        public List<PinnedApp> Pins { get; set; } = new();
        /// <summary>Fokus (Mond in der Insel): keine Song-Hinweise, nur Erinnerungen.</summary>
        public bool FocusMode { get; set; }
        public List<ReminderItem> Reminders { get; set; } = new();
        public bool PinsInitialized { get; set; }
        /// <summary>Isle empfängt die Tray-Icons selbst und zeigt sie unter dem Pfeil in der Top-Bar.</summary>
        public bool TrayInTopBar { get; set; } = true;
        /// <summary>Schriftfarbe der Top-Bar: "auto" (nach Hintergrund), "dark" oder "light".</summary>
        public string BarTextMode { get; set; } = "auto";
        /// <summary>Schriftstärke der Top-Bar: 0 Dünn … 4 Fett (2 = Normal).</summary>
        public int BarFontWeight { get; set; } = 2;
        /// <summary>Apps aus der Windows-Taskleiste wurden schon einmal übernommen.</summary>
        public bool TaskbarImported { get; set; }
        /// <summary>Taskleisten-Zustand vor Isle – wird beim Beenden wiederhergestellt.</summary>
        public int? OriginalTaskbarState { get; set; }

        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public static Settings Current { get; private set; } = new();

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch
            {
                Current = new Settings();
            }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                File.Move(tmp, FilePath, true);
            }
            catch { /* Speichern ist nicht kritisch */ }
        }

        public static event Action? Changed;
        public static void NotifyChanged()
        {
            Save();
            Changed?.Invoke();
        }
    }
}
