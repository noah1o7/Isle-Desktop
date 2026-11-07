using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle
{
    internal static class ColorHelper
    {
        /// <summary>
        /// Holt eine kräftige, gut lesbare Akzentfarbe aus dem Cover.
        /// Gibt null zurück, wenn das Bild praktisch grau ist.
        /// </summary>
        public static Color? AccentFrom(BitmapSource source)
        {
            try
            {
                const int n = 24;
                var scaled = new TransformedBitmap(source,
                    new ScaleTransform((double)n / source.PixelWidth, (double)n / source.PixelHeight));
                var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                int w = bgra.PixelWidth, h = bgra.PixelHeight;
                var px = new byte[w * h * 4];
                bgra.CopyPixels(px, w * 4, 0);

                double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                    RgbToHsv(r, g, b, out _, out double s, out double v);
                    // Bunte, nicht zu dunkle Pixel zählen viel mehr
                    double weight = s * s * Math.Max(v - 0.15, 0) + 0.0005;
                    sumR += r * weight; sumG += g * weight; sumB += b * weight; sumW += weight;
                }

                if (sumW < 0.4) return null; // fast schwarz-weiß

                RgbToHsv(sumR / sumW, sumG / sumW, sumB / sumW, out double hue, out double sat, out double val);
                sat = Math.Clamp(sat * 1.15, 0.35, 0.85);
                val = Math.Max(val, 0.82); // auf schwarzem Grund gut sichtbar
                HsvToRgb(hue, sat, val, out double rr, out double gg, out double bb);
                return Color.FromRgb((byte)(rr * 255), (byte)(gg * 255), (byte)(bb * 255));
            }
            catch
            {
                return null;
            }
        }

        private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) { h = 0; return; }
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            if (h < 0) h += 360;
        }

        private static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            (r, g, b) = h switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x),
            };
            r += m; g += m; b += m;
        }
    }
}
