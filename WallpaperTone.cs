using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Isle
{
    /// <summary>
    /// Misst, wie hell das Hintergrundbild genau dort ist, wo die Top-Bar liegt – links und rechts getrennt.
    /// Berücksichtigt, wie das Bild angezeigt wird (Ausfüllen, Anpassen, Strecken, Zentriert, Kachel).
    /// Ergebnis: Helligkeit 0 (schwarz) … 1 (weiß).
    /// </summary>
    internal static class WallpaperTone
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buf, uint winIni);
        [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
        private const uint SPI_GETDESKWALLPAPER = 0x73;
        private const int COLOR_DESKTOP = 1;

        public sealed record Result(double Left, double Right, string? Source, DateTime SourceTime);

        /// <summary>Pfad des aktuellen Hintergrundbilds (oder null bei einfarbigem Hintergrund).</summary>
        public static string? CurrentPath()
        {
            try
            {
                var sb = new StringBuilder(520);
                if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0))
                {
                    string p = sb.ToString();
                    if (p.Length > 0 && File.Exists(p)) return p;
                }
                // Diashow, Windows-Blickpunkt usw.: Windows legt das aktuelle Bild hier ab
                string transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                 @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(transcoded)) return transcoded;
            }
            catch { }
            return null;
        }

        /// <param name="screenW">Bildschirmbreite in Pixeln</param>
        /// <param name="screenH">Bildschirmhöhe in Pixeln</param>
        /// <param name="barH">Höhe der Top-Bar in Pixeln</param>
        public static Result Measure(int screenW, int screenH, int barH)
        {
            string? path = CurrentPath();
            if (path == null)
            {
                Log.Write("Kein Hintergrundbild gefunden – nutze Hintergrundfarbe");
                return Solid(null);
            }
            DateTime stamp = File.GetLastWriteTimeUtc(path);

            try
            {
                // Originalgröße lesen, ohne das ganze Bild zu laden
                int origW, origH;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    origW = dec.Frames[0].PixelWidth;
                    origH = dec.Frames[0].PixelHeight;
                }
                if (origW <= 0 || origH <= 0) return Solid(path);

                // Verkleinert laden – für eine Durchschnittshelligkeit reicht das völlig und ist schnell
                var bi = new BitmapImage();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = fs;
                    bi.DecodePixelWidth = Math.Min(origW, 640);
                    bi.EndInit();
                }
                bi.Freeze();
                double k = bi.PixelWidth / (double)origW; // Original → verkleinert

                var (style, tile) = ReadStyle();
                Rect leftScreen = new(0, 0, screenW * 0.40, barH);
                Rect rightScreen = new(screenW * 0.60, 0, screenW * 0.40, barH);

                double? left = MeasureRegion(bi, leftScreen, origW, origH, screenW, screenH, style, tile, k);
                double? right = MeasureRegion(bi, rightScreen, origW, origH, screenW, screenH, style, tile, k);
                double bg = Luma(GetSysColor(COLOR_DESKTOP));
                return new Result(left ?? bg, right ?? bg, path, stamp);
            }
            catch (Exception ex)
            {
                Log.Write($"Hintergrundbild „{path}“ ließ sich nicht auswerten: {ex.Message}");
                return Solid(path);
            }
        }

        private static Result Solid(string? path)
        {
            double l = Luma(GetSysColor(COLOR_DESKTOP));
            return new Result(l, l, path, path != null ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue);
        }

        private static (int style, bool tile) ReadStyle()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                int.TryParse(key?.GetValue("WallpaperStyle") as string, out int style);
                bool tile = (key?.GetValue("TileWallpaper") as string) == "1";
                return (style, tile);
            }
            catch { return (10, false); }
        }

        /// <summary>Welcher Teil des Bilds liegt hinter diesem Bildschirmbereich? → dessen Helligkeit (null = nur Hintergrundfarbe).</summary>
        private static double? MeasureRegion(BitmapSource img, Rect screen, int iw, int ih, int sw, int sh, int style, bool tile, double k)
        {
            double scaleX, scaleY, offX, offY;
            if (tile)
            {
                // Kachel: oben links beginnt das Bild, wiederholt sich – Ausschnitt aus der ersten Kachel reicht
                scaleX = scaleY = 1;
                offX = offY = 0;
                screen = new Rect(screen.X % iw, 0, Math.Min(screen.Width, iw), Math.Min(screen.Height, ih));
            }
            else switch (style)
            {
                case 2: // Strecken
                    scaleX = sw / (double)iw; scaleY = sh / (double)ih; offX = offY = 0;
                    break;
                case 6: // Anpassen (ganzes Bild sichtbar, Ränder in Hintergrundfarbe)
                {
                    double s = Math.Min(sw / (double)iw, sh / (double)ih);
                    scaleX = scaleY = s; offX = (sw - iw * s) / 2; offY = (sh - ih * s) / 2;
                    break;
                }
                case 0: // Zentriert (Originalgröße)
                    scaleX = scaleY = 1; offX = (sw - iw) / 2.0; offY = (sh - ih) / 2.0;
                    break;
                default: // 10 Ausfüllen, 22 Strecken über Monitore, unbekannt
                {
                    double s = Math.Max(sw / (double)iw, sh / (double)ih);
                    scaleX = scaleY = s; offX = (sw - iw * s) / 2; offY = (sh - ih * s) / 2;
                    break;
                }
            }

            // Bildschirm → Originalbild → verkleinertes Bild
            double x0 = (screen.Left - offX) / scaleX, x1 = (screen.Right - offX) / scaleX;
            double y0 = (screen.Top - offY) / scaleY, y1 = (screen.Bottom - offY) / scaleY;
            x0 = Math.Clamp(x0, 0, iw); x1 = Math.Clamp(x1, 0, iw);
            y0 = Math.Clamp(y0, 0, ih); y1 = Math.Clamp(y1, 0, ih);
            if (x1 - x0 < 1 || y1 - y0 < 0.5) return null; // Bereich liegt außerhalb des Bilds

            int px = (int)Math.Floor(x0 * k), py = (int)Math.Floor(y0 * k);
            int pw = Math.Max(1, (int)Math.Ceiling((x1 - x0) * k)), ph = Math.Max(1, (int)Math.Ceiling((y1 - y0) * k));
            pw = Math.Min(pw, img.PixelWidth - px);
            ph = Math.Min(ph, img.PixelHeight - py);
            if (pw <= 0 || ph <= 0) return null;

            var crop = new CroppedBitmap(img, new Int32Rect(px, py, pw, ph));
            var bgra = new FormatConvertedBitmap(crop, PixelFormats.Bgra32, null, 0);
            var buf = new byte[pw * ph * 4];
            bgra.CopyPixels(buf, pw * 4, 0);

            double sum = 0;
            for (int i = 0; i < buf.Length; i += 4)
                sum += (0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2]) / 255.0;
            return sum / (pw * ph);
        }

        private static double Luma(uint colorref)
        {
            double r = colorref & 0xFF, g = (colorref >> 8) & 0xFF, b = (colorref >> 16) & 0xFF;
            return (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        }
    }
}
