using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Isle
{
    /// <summary>Wetter über Open-Meteo (kostenlos, kein API-Schlüssel).</summary>
    internal static class Weather
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public sealed record Result(double Temperature, string Symbol, string Description);

        public static async Task<(double lat, double lon, string name)?> GeocodeAsync(string city)
        {
            try
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=de&format=json&name="
                             + Uri.EscapeDataString(city);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
                var r = results[0];
                return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), r.GetProperty("name").GetString() ?? city);
            }
            catch { return null; }
        }

        public static async Task<Result?> CurrentAsync(double lat, double lon)
        {
            try
            {
                string url = string.Format(CultureInfo.InvariantCulture,
                    "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}&current=temperature_2m,weather_code,is_day",
                    lat, lon);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var cur = doc.RootElement.GetProperty("current");
                double temp = cur.GetProperty("temperature_2m").GetDouble();
                int code = cur.GetProperty("weather_code").GetInt32();
                bool day = cur.GetProperty("is_day").GetInt32() == 1;
                var (sym, desc) = Describe(code, day);
                return new Result(temp, sym, desc);
            }
            catch { return null; }
        }

        private static (string, string) Describe(int code, bool day) => code switch
        {
            0 => (day ? "☀" : "☾", "Klar"),
            1 or 2 => (day ? "⛅" : "☾", "Leicht bewölkt"),
            3 => ("☁", "Bewölkt"),
            45 or 48 => ("≋", "Nebel"),
            >= 51 and <= 57 => ("☂", "Nieselregen"),
            >= 61 and <= 67 => ("☂", "Regen"),
            >= 71 and <= 77 => ("❄", "Schnee"),
            >= 80 and <= 82 => ("☂", "Schauer"),
            85 or 86 => ("❄", "Schneeschauer"),
            >= 95 => ("⚡", "Gewitter"),
            _ => ("☁", ""),
        };
    }

    /// <summary>Erkennt, ob gerade eine App Mikrofon oder Kamera benutzt (wie die Windows-Datenschutzanzeige).</summary>
    internal static class PrivacyMonitor
    {
        private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

        public static bool InUse(string capability)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Root + capability);
                return key != null && Scan(key, 0);
            }
            catch { return false; }
        }

        private static bool Scan(RegistryKey key, int depth)
        {
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub == null) continue;
                if (sub.GetValue("LastUsedTimeStop") is long stop && sub.GetValue("LastUsedTimeStart") is long start
                    && stop == 0 && start != 0)
                    return true;
                if (depth == 0 && name == "NonPackaged" && Scan(sub, 1)) return true;
            }
            return false;
        }
    }
}
