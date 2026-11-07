using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Isle
{
    /// <summary>Symbol im Infobereich mit dunklem Menü: Autostart an/aus, Beenden.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly IntPtr _hIcon;

        public TrayIcon()
        {
            var menu = new WinForms.ContextMenuStrip
            {
                Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false },
                BackColor = Color.FromArgb(28, 28, 30),
                ForeColor = Color.FromArgb(245, 245, 247),
                ShowImageMargin = false,
                Font = new Font("Segoe UI", 9.5f),
            };

            var autostart = new WinForms.ToolStripMenuItem("Mit Windows starten")
            {
                Checked = Autostart.IsEnabled(),
                CheckOnClick = true,
                ForeColor = menu.ForeColor,
            };
            autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);

            var quit = new WinForms.ToolStripMenuItem("Beenden   (Strg+Alt+Q)") { ForeColor = menu.ForeColor };
            quit.Click += (_, _) => ((App)System.Windows.Application.Current).Quit();

            menu.Items.Add(new WinForms.ToolStripLabel("Isle") { ForeColor = Color.FromArgb(142, 142, 147) });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(autostart);
            menu.Items.Add(quit);

            var bmp = DrawIcon();
            _hIcon = bmp.GetHicon();
            bmp.Dispose();

            _icon = new WinForms.NotifyIcon
            {
                Icon = Icon.FromHandle(_hIcon),
                Text = "Isle – Dynamic Island",
                ContextMenuStrip = menu,
                Visible = true,
            };
        }

        /// <summary>Kleine helle Pille, gut sichtbar auf dunkler Taskleiste.</summary>
        private static Bitmap DrawIcon()
        {
            var bmp = new Bitmap(32, 32);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            var r = new RectangleF(2, 10, 28, 12);
            path.AddArc(r.X, r.Y, r.Height, r.Height, 90, 180);
            path.AddArc(r.Right - r.Height, r.Y, r.Height, r.Height, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(245, 245, 247));
            g.FillPath(fill, path);
            using var dot = new SolidBrush(Color.FromArgb(10, 10, 12));
            g.FillEllipse(dot, 21, 13, 6, 6);
            return bmp;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private sealed class DarkColors : WinForms.ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(28, 28, 30);
            private static readonly Color Hover = Color.FromArgb(58, 58, 60);
            private static readonly Color Line = Color.FromArgb(58, 58, 60);

            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Bg;
            public override Color CheckBackground => Hover;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }
    }

    /// <summary>Eintrag unter HKCU\…\Run, damit Isle beim Anmelden startet.</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "Isle";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }

        public static void Set(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
                else key.DeleteValue(RunName, false);
            }
            catch { }
        }
    }
}
