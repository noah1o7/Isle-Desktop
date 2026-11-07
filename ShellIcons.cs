using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle
{
    /// <summary>Eigener STA-Thread für Shell-COM-Aufrufe, damit die Oberfläche flüssig bleibt.</summary>
    internal static class StaWorker
    {
        private static readonly BlockingCollection<Action> Queue = new();

        static StaWorker()
        {
            var t = new Thread(() =>
            {
                foreach (var job in Queue.GetConsumingEnumerable())
                {
                    try { job(); } catch { }
                }
            })
            { IsBackground = true, Name = "Isle.Shell" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public static void Run(Action job) => Queue.Add(job);
    }

    /// <summary>Lädt scharfe, große Icons über die Windows-Shell (wie im Explorer), mit Cache.</summary>
    internal static class ShellIcons
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object item);

        private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

        private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, List<Action<ImageSource?>>> Pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Liefert das Icon asynchron. Der Rückruf läuft auf dem UI-Thread.
        /// keys: mehrere Kandidaten, der erste der klappt gewinnt (z. B. App-ID, dann Exe-Pfad).
        /// </summary>
        public static void Get(int size, bool iconOnly, Action<ImageSource?> callback, params string?[] keys)
        {
            string cacheKey = size + "|" + iconOnly + "|" + string.Join("|", keys);
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                callback(cached);
                return;
            }

            var dispatcher = Application.Current.Dispatcher;
            bool first = false;
            var list = Pending.GetOrAdd(cacheKey, _ => { first = true; return new List<Action<ImageSource?>>(); });
            lock (list) list.Add(callback);
            if (!first) return;

            StaWorker.Run(() =>
            {
                ImageSource? img = null;
                foreach (var k in keys)
                {
                    if (string.IsNullOrWhiteSpace(k)) continue;
                    img = Load(k!, size, iconOnly);
                    if (img != null) break;
                }
                Cache[cacheKey] = img;
                Pending.TryRemove(cacheKey, out var waiting);
                dispatcher.BeginInvoke(() =>
                {
                    if (waiting == null) return;
                    List<Action<ImageSource?>> copy;
                    lock (waiting) copy = new List<Action<ImageSource?>>(waiting);
                    foreach (var cb in copy) cb(img);
                });
            });
        }

        private static ImageSource? Load(string parsingName, int size, bool iconOnly)
        {
            object? obj = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
                if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out obj) != 0 || obj == null)
                    return null;
                var factory = (IShellItemImageFactory)obj;
                int flags = SIIGBF_BIGGERSIZEOK | (iconOnly ? SIIGBF_ICONONLY : 0);
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero)
                    return null;
                return FromHBitmap(hbm);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hbm != IntPtr.Zero) Win32.DeleteObject(hbm);
                if (obj != null) Marshal.ReleaseComObject(obj);
            }
        }

        /// <summary>HBITMAP → WPF-Bild, mit Transparenz (die Standard-Umwandlung verliert den Alphakanal).</summary>
        private static BitmapSource? FromHBitmap(IntPtr hbm)
        {
            if (Win32.GetObject(hbm, Marshal.SizeOf<Win32.BITMAP>(), out var bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;

            var bi = new Win32.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // von oben nach unten
                biPlanes = 1,
                biBitCount = 32,
            };
            var bits = new byte[w * h * 4];
            IntPtr hdc = Win32.GetDC(IntPtr.Zero);
            try
            {
                if (Win32.GetDIBits(hdc, hbm, 0, (uint)h, bits, ref bi, 0) == 0) return null;
            }
            finally { Win32.ReleaseDC(IntPtr.Zero, hdc); }

            // Alte Icons ohne Alphakanal sonst unsichtbar → dann voll deckend machen
            bool anyAlpha = false;
            for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;

            var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
            src.Freeze();
            return src;
        }
    }
}
