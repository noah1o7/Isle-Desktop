using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>
    /// Vorschau über einem Dock-Icon: ein Kärtchen pro offenem Fenster mit Titel, Schließen-Knopf
    /// und Live-Bild (die Windows-eigene DWM-Vorschau, wie bei der normalen Taskleiste).
    /// Absichtlich NICHT transparent – DWM-Live-Bilder erscheinen nur auf normalen Fenstern.
    /// </summary>
    internal sealed class WindowPreview : Window
    {
        private const double CellW = 230, ThumbH = 128, TitleH = 30, Pad = 8, Gap = 6;
        private const int MaxCols = 5;
        private static double CellH => TitleH + ThumbH + 8;

        private sealed record Win(IntPtr Hwnd, string Title, bool Iconic);

        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(700) };
        private readonly Dictionary<IntPtr, IntPtr> _thumbs = new();
        private readonly List<(Win win, Border area, UIElement placeholder)> _cells = new();
        private List<Win> _wins = new();
        private Func<RunningApp?>? _source;
        private ImageSource? _icon;
        private double _anchorX, _anchorBottom;
        private IntPtr _hwnd;

        public string? Key { get; private set; }
        public event Action? WindowActivated;

        public WindowPreview()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            UseLayoutRounding = true;
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
            _refresh.Tick += (_, _) => Refresh();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeToolWindow(_hwnd);
            int round = 2;                       // Windows 11: runde Ecken
            Win32.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
            int border = 0x003C3A3A;             // dezenter Rand #3A3A3C (BGR)
            Win32.DwmSetWindowAttribute(_hwnd, 34, ref border, sizeof(int));
        }

        // ---------------------------------------------------------------- Anzeigen

        /// <param name="centerX">Mitte des Icons (geräteunabhängige Pixel)</param>
        /// <param name="bottom">Unterkante der Vorschau (über dem Dock)</param>
        public bool ShowFor(string key, Func<RunningApp?> source, ImageSource? icon, double centerX, double bottom)
        {
            Key = key;
            _source = source;
            _icon = icon;
            _anchorX = centerX;
            _anchorBottom = bottom;
            var wins = Collect();
            if (wins.Count == 0)
            {
                HidePreview();
                return false;
            }
            Apply(wins);
            return true;
        }

        public void HidePreview()
        {
            _refresh.Stop();
            DropThumbs();
            if (IsVisible) Hide();
            Key = null;
        }

        public bool IsCursorOver() =>
            IsVisible && _hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out var r) && Win32.CursorIn(r);

        private List<Win> Collect()
        {
            var app = _source?.Invoke();
            if (app == null) return new();
            return WindowTracker.LiveWindows(app)
                .Select(h => new Win(h, Win32.WindowTitle(h), Win32.IsIconic(h)))
                .Where(w => w.Title.Length > 0)
                .ToList();
        }

        private void Refresh()
        {
            if (!IsVisible) { _refresh.Stop(); return; }
            var wins = Collect();
            if (wins.Count == 0) HidePreview();
            else if (!wins.SequenceEqual(_wins)) Apply(wins);
        }

        private void Apply(List<Win> wins)
        {
            DropThumbs();
            _wins = wins;
            _cells.Clear();

            var screen = SystemParameters.WorkArea;
            int maxFit = (int)((screen.Width - 16 - 2 * Pad + Gap) / (CellW + Gap));
            int cols = Math.Max(1, Math.Min(Math.Min(MaxCols, maxFit), wins.Count));
            int rows = (int)Math.Ceiling(wins.Count / (double)cols);
            double w = 2 * Pad + cols * CellW + (cols - 1) * Gap;
            double h = 2 * Pad + rows * CellH + (rows - 1) * Gap;

            var canvas = new Canvas { Width = w - 2 * Pad, Height = h - 2 * Pad, Margin = new Thickness(Pad) };
            for (int i = 0; i < wins.Count; i++)
            {
                var cell = MakeCell(wins[i]);
                Canvas.SetLeft(cell, (i % cols) * (CellW + Gap));
                Canvas.SetTop(cell, (i / cols) * (CellH + Gap));
                canvas.Children.Add(cell);
            }
            Content = canvas;

            Width = w;
            Height = h;
            Left = Math.Clamp(_anchorX - w / 2, screen.Left + 8, screen.Right - w - 8);
            Top = _anchorBottom - h;

            if (!IsVisible) Show();
            UpdateLayout();
            RegisterThumbs();
            _refresh.Start();
        }

        // ---------------------------------------------------------------- Kärtchen

        private Border MakeCell(Win win)
        {
            var primary = (Brush)Application.Current.Resources["TextPrimary"];
            var secondary = (Brush)Application.Current.Resources["TextSecondary"];

            var icon = new Image { Source = _icon, Width = 16, Height = 16, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            var title = new TextBlock
            {
                Text = win.Title, Foreground = primary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var closeX = new Path
            {
                Data = Geometry.Parse("M0,0 L9,9 M0,9 L9,0"), Stroke = primary, StrokeThickness = 1.4,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var close = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 4, 0),
                Background = Brushes.Transparent, Child = closeX, Visibility = Visibility.Hidden, Cursor = Cursors.Hand,
                ToolTip = "Fenster schließen",
            };
            close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
            close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;

            var head = new Grid { Height = TitleH };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(icon);
            Grid.SetColumn(title, 1);
            head.Children.Add(title);
            Grid.SetColumn(close, 2);
            head.Children.Add(close);

            // Platzhalter (Icon + Text) – wird vom Live-Bild überdeckt, falls es eins gibt
            var ph = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ph.Children.Add(new Image { Source = _icon, Width = 44, Height = 44 });
            ph.Children.Add(new TextBlock
            {
                Text = win.Iconic ? "Minimiert" : "Keine Vorschau", Foreground = secondary, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
            });
            var area = new Border
            {
                Margin = new Thickness(6, 0, 6, 8), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)), Child = ph,
            };

            var stack = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
            stack.Children.Add(head);
            stack.Children.Add(area);

            var cell = new Border
            {
                Width = CellW, Height = CellH, CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent, Child = stack, Cursor = Cursors.Hand,
            };
            cell.MouseEnter += (_, _) => { cell.Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)); close.Visibility = Visibility.Visible; };
            cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; close.Visibility = Visibility.Hidden; };
            cell.MouseUp += (_, e) =>
            {
                e.Handled = true;
                bool onClose = close.IsMouseOver;
                if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && onClose))
                {
                    WindowTracker.Close(win.Hwnd);
                    var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    t.Tick += (_, _) => { t.Stop(); Refresh(); };
                    t.Start();
                }
                else if (e.ChangedButton == MouseButton.Left)
                {
                    HidePreview();
                    Win32.ForceForeground(win.Hwnd);
                    WindowActivated?.Invoke();
                }
            };

            _cells.Add((win, area, ph));
            return cell;
        }

        // ---------------------------------------------------------------- Live-Bilder

        private void RegisterThumbs()
        {
            if (_hwnd == IntPtr.Zero) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            foreach (var (win, area, placeholder) in _cells)
            {
                if (win.Iconic) continue; // minimierte Fenster haben kein Live-Bild
                try
                {
                    if (Win32.DwmRegisterThumbnail(_hwnd, win.Hwnd, out IntPtr th) != 0 || th == IntPtr.Zero) continue;
                    if (Win32.DwmQueryThumbnailSourceSize(th, out var src) != 0 || src.cx <= 0 || src.cy <= 0)
                    {
                        Win32.DwmUnregisterThumbnail(th);
                        continue;
                    }

                    var pos = area.TranslatePoint(new Point(0, 0), this);
                    double ax = pos.X * dpi.DpiScaleX, ay = pos.Y * dpi.DpiScaleY;
                    double aw = area.ActualWidth * dpi.DpiScaleX, ah = area.ActualHeight * dpi.DpiScaleY;
                    double scale = Math.Min(Math.Min(aw / src.cx, ah / src.cy), 1.0);
                    int tw = (int)(src.cx * scale), tht = (int)(src.cy * scale);
                    int left = (int)(ax + (aw - tw) / 2), top = (int)(ay + (ah - tht) / 2);

                    var props = new Win32.DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_OPACITY | Win32.DWM_TNP_VISIBLE | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
                        rcDestination = new Win32.RECT { Left = left, Top = top, Right = left + tw, Bottom = top + tht },
                        opacity = 255,
                        fVisible = 1,
                        fSourceClientAreaOnly = 0,
                    };
                    Win32.DwmUpdateThumbnailProperties(th, ref props);
                    _thumbs[win.Hwnd] = th;
                    placeholder.Visibility = Visibility.Hidden;
                }
                catch { }
            }
        }

        private void DropThumbs()
        {
            foreach (var th in _thumbs.Values) Win32.DwmUnregisterThumbnail(th);
            _thumbs.Clear();
        }

        protected override void OnClosed(EventArgs e)
        {
            DropThumbs();
            _refresh.Stop();
            base.OnClosed(e);
        }
    }
}
