using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Isle
{
    /// <summary>
    /// Misst die Helligkeit dessen, was wirklich HINTER der Top-Bar auf dem Bildschirm ist
    /// (Hintergrundbild oder ein Fenster), links und rechts getrennt.
    ///
    /// Damit die Leiste ihre eigene Schrift nicht mitmisst, wird sie für den Bruchteil einer Sekunde
    /// aus Bildschirmaufnahmen ausgeschlossen (Windows 10 ab 2004 / Windows 11). Auf dem Bildschirm
    /// bleibt sie dabei ganz normal sichtbar.
    /// </summary>
    internal static class ScreenTone
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        private const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
        private const uint SRCCOPY = 0x00CC0020;

        /// <returns>(links, rechts) Helligkeit 0…1, oder null wenn es auf diesem Windows nicht geht</returns>
        public static async Task<(double left, double right)?> SampleAsync(IntPtr barHwnd, int barHeightPx)
        {
            if (barHwnd == IntPtr.Zero) return null;
            if (!SetWindowDisplayAffinity(barHwnd, WDA_EXCLUDEFROMCAPTURE))
            {
                Log.Write($"Bildschirm-Messung nicht möglich (Fehler {Marshal.GetLastWin32Error()}) – nutze Bilddatei");
                return null;
            }
            try
            {
                await Task.Delay(120); // Windows braucht ein, zwei Bilder, bis der Ausschluss greift
                return Capture(barHeightPx);
            }
            catch (Exception ex)
            {
                Log.Write("Bildschirm-Messung fehlgeschlagen: " + ex.Message);
                return null;
            }
            finally
            {
                SetWindowDisplayAffinity(barHwnd, WDA_NONE);
            }
        }

        private static (double left, double right)? Capture(int barH)
        {
            var mon = Win32.PrimaryMonitorRect();
            int w = mon.Right - mon.Left;
            int h = Math.Max(4, barH - 4); // nicht ganz bis zum unteren Rand (dort beginnen Fenster)
            if (w <= 0) return null;

            IntPtr screen = Win32.GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
            IntPtr old = SelectObject(mem, bmp);
            byte[] px;
            try
            {
                if (!BitBlt(mem, 0, 0, w, h, screen, mon.Left, mon.Top + 2, SRCCOPY)) return null;
                SelectObject(mem, old);
                old = IntPtr.Zero;

                var bi = new Win32.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                };
                px = new byte[w * h * 4];
                if (Win32.GetDIBits(screen, bmp, 0, (uint)h, px, ref bi, 0) == 0) return null;
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                Win32.DeleteObject(bmp);
                DeleteDC(mem);
                Win32.ReleaseDC(IntPtr.Zero, screen);
            }

            // Links 0–40 %, rechts 60–100 % (in der Mitte sitzt die Insel)
            double left = Average(px, w, h, 0, (int)(w * 0.40));
            double right = Average(px, w, h, (int)(w * 0.60), w);
            return (left, right);
        }

        private static double Average(byte[] px, int w, int h, int x0, int x1)
        {
            double sum = 0;
            int n = 0;
            for (int y = 0; y < h; y += 2)
            {
                int row = y * w * 4;
                for (int x = x0; x < x1; x += 3)
                {
                    int i = row + x * 4;
                    sum += (0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2]) / 255.0;
                    n++;
                }
            }
            return n == 0 ? 0 : sum / n;
        }
    }

    /// <summary>Kleines Protokoll unter %AppData%\Isle\isle.log – hilft bei der Fehlersuche.</summary>
    internal static class Log
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isle", "isle.log");
        private static readonly object Gate = new();

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 256 * 1024) File.Delete(FilePath); // klein halten
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
