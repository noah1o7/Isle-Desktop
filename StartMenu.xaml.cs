using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Isle
{
    public partial class StartMenu : Window
    {
        private readonly Dictionary<AppEntry, Button> _appTiles = new();
        private readonly List<Button> _visible = new();
        private int _selected = -1;
        private bool _closing;

        public DateTime LastHidden { get; private set; } = DateTime.MinValue;
        private DateTime _shownAt = DateTime.MinValue;
        private ContextMenu? _openSub; // gerade offenes Rechtsklick-/Ein-Aus-Menü

        /// <summary>Menü ist (nach der Ausblend-Animation) komplett weg.</summary>
        public event Action? Hidden;

        public StartMenu()
        {
            InitializeComponent();
            UserName.Text = Environment.UserName;
            AvatarLetter.Text = Environment.UserName.Length > 0 ? Environment.UserName[..1].ToUpperInvariant() : "?";

            AppCatalog.Loaded += BuildAppTiles;
            Deactivated += (_, _) => HideMenu();
        }

        // ---------------------------------------------------------------- Ein-/Ausblenden

        public void Toggle()
        {
            if (IsVisible) { HideMenu(); return; }
            // Klick auf den Dock-Start-Knopf hat das Menü gerade erst geschlossen → nicht sofort wieder öffnen
            if ((DateTime.Now - LastHidden).TotalMilliseconds < 300) return;
            ShowMenu();
        }

        public void ShowMenu()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);

            _closing = false; // falls eine Ausblend-Animation unterbrochen wurde – sonst ginge das Menü nie mehr zu
            _openSub = null;
            _shownAt = DateTime.Now;
            SearchBox.Text = "";
            BuildRecent();
            ApplyFilter();
            Scroller.ScrollToTop();

            Opacity = 0;
            Show();
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.ForceForeground(hwnd);
            Activate();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            StartWatch(); // Klick daneben / andere App vorne → schließen, auch ohne Fokus

            var dur = TimeSpan.FromMilliseconds(160);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        }

        public void HideMenu()
        {
            if (!IsVisible || _closing) return;
            _closing = true;
            LastHidden = DateTime.Now;
            StopWatch();
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
            a.Completed += (_, _) =>
            {
                if (!_closing) return; // inzwischen wieder geöffnet
                Hide();
                _closing = false;
                Hidden?.Invoke();
            };
            BeginAnimation(OpacityProperty, a);
        }

        // ---------------------------------------------------------------- Schließen, wenn man woanders hinklickt
        //
        // Bewusst ohne Maus-Hook und ohne sich auf WPFs „Deactivated“ zu verlassen: Beides fällt aus, wenn Windows
        // dem Menü den Fokus nicht gibt oder Isle kurz beschäftigt ist. Stattdessen schaut ein Timer alle 40 ms nach:
        //  • Maustaste neu gedrückt und Zeiger außerhalb des Menüs → zu
        //  • eine andere App ist nach vorne gekommen → zu

        private DispatcherTimer? _watch;
        private bool _btnWasDown;
        private bool _hadFocus;
        private IntPtr _fgAtShow;

        private void StartWatch()
        {
            _btnWasDown = MouseButtonDown();
            _hadFocus = false;
            _fgAtShow = Win32.GetForegroundWindow();
            _watch ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
            _watch.Tick -= Watch_Tick;
            _watch.Tick += Watch_Tick;
            _watch.Start();
        }

        private void StopWatch() => _watch?.Stop();

        private static bool MouseButtonDown() =>
            Win32.GetAsyncKeyState(0x01) < 0 || Win32.GetAsyncKeyState(0x02) < 0 || Win32.GetAsyncKeyState(0x04) < 0; // oberstes Bit = gedrückt

        private void Watch_Tick(object? sender, EventArgs e)
        {
            if (!IsVisible || _closing) { StopWatch(); return; }
            IntPtr me = new WindowInteropHelper(this).Handle;
            bool subOpen = _openSub?.IsOpen == true; // Rechtsklick- oder Ein/Aus-Menü offen: das regelt sich selbst
            double age = (DateTime.Now - _shownAt).TotalMilliseconds;

            // 1) Klick außerhalb
            bool btn = MouseButtonDown();
            if (btn && !_btnWasDown && !subOpen && Win32.GetCursorPos(out var p) && Win32.GetWindowRect(me, out var r))
            {
                bool inside = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
                if (!inside) { _btnWasDown = btn; HideMenu(); return; }
            }
            _btnWasDown = btn;

            // 2) Fokus: hat Windows uns (noch) nicht nach vorne gelassen → kurz nach dem Öffnen nochmal versuchen
            IntPtr fg = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(fg, out uint pid);
            bool ours = fg == me || pid == (uint)Environment.ProcessId;
            if (ours) { _hadFocus = true; return; }
            if (!_hadFocus && age < 600)
            {
                Win32.ForceForeground(me);
                return;
            }

            // 3) Eine andere App ist nach vorne gekommen (Alt+Tab, Klick in die Taskleiste, App gestartet …)
            if (subOpen) return;
            if (_hadFocus || fg != _fgAtShow) HideMenu();
        }

        /// <summary>Kleine Menüs im Startmenü öffnen und mitzählen, solange sie offen sind.</summary>
        private void OpenSubMenu(ContextMenu m)
        {
            _openSub = m;
            m.IsOpen = true;
        }

        // ---------------------------------------------------------------- Kacheln

        private Button MakeTile(string title, Action onClick, Action<Image> loadIcon, Func<ContextMenu?>? menu = null)
        {
            var img = new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var text = new TextBlock
            {
                Text = title,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                Width = 94,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var stack = new StackPanel();
            stack.Children.Add(img);
            stack.Children.Add(text);

            var b = new Button { Style = (Style)FindResource("TileButton"), Content = stack, ToolTip = title };
            b.Click += (_, _) => { HideMenu(); onClick(); };
            if (menu != null)
            {
                b.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var m = menu();
                    if (m == null) return;
                    m.PlacementTarget = b;
                    m.Placement = PlacementMode.MousePoint;
                    OpenSubMenu(m);
                };
            }
            loadIcon(img);
            return b;
        }

        private void BuildAppTiles()
        {
            _appTiles.Clear();
            foreach (var app in AppCatalog.Apps)
            {
                var a = app;
                _appTiles[a] = MakeTile(a.Name,
                    () => AppCatalog.Launch(a.Id, a.ExePath),
                    img => ShellIcons.Get(96, true, src => img.Source = src, a.IconKey, a.ExePath),
                    () => AppMenu(a));
            }
            if (IsVisible) ApplyFilter();
        }

        private ContextMenu AppMenu(AppEntry a)
        {
            var app = (App)Application.Current;
            var m = new ContextMenu();
            m.Items.Add(TopBar.Item("Öffnen", () => { HideMenu(); AppCatalog.Launch(a.Id, a.ExePath); }));
            if (a.ExePath != null)
            {
                m.Items.Add(TopBar.Item("Als Administrator ausführen", () =>
                {
                    HideMenu();
                    try { Process.Start(new ProcessStartInfo(a.ExePath) { UseShellExecute = true, Verb = "runas" }); } catch { }
                }));
            }
            m.Items.Add(new Separator());
            if (app.Dock != null && app.Dock.IsPinned(a.Id))
                m.Items.Add(TopBar.Item("Aus dem Dock entfernen", () => app.Dock.Unpin(a.Id)));
            else
                m.Items.Add(TopBar.Item("Im Dock behalten", () => app.Dock?.Pin(a)));
            if (a.ExePath != null)
            {
                m.Items.Add(TopBar.Item("Dateispeicherort öffnen", () =>
                {
                    HideMenu();
                    try { Process.Start("explorer.exe", $"/select,\"{a.ExePath}\""); } catch { }
                }));
            }
            return m;
        }

        private void BuildRecent()
        {
            RecentPanel.Children.Clear();
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                var files = new DirectoryInfo(dir).GetFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(6);
                foreach (var f in files)
                {
                    string path = f.FullName;
                    RecentPanel.Children.Add(MakeTile(Path.GetFileNameWithoutExtension(f.Name),
                        () => AppCatalog.Open(path),
                        img => ShellIcons.Get(96, false, src => img.Source = src, path)));
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- Suche

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            string q = SearchBox.Text.Trim();
            Placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool searching = q.Length > 0;
            RecentHeader.Visibility = RecentPanel.Visibility =
                !searching && RecentPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppsHeader.Text = searching ? "Ergebnisse" : "Alle Apps";

            IEnumerable<AppEntry> apps = AppCatalog.Apps;
            if (searching)
            {
                apps = AppCatalog.Apps
                    .Select(a => (a, score: Score(a.Name, q)))
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(x => x.a);
            }

            AppsPanel.Children.Clear();
            _visible.Clear();
            foreach (var a in apps)
            {
                if (!_appTiles.TryGetValue(a, out var tile)) continue;
                tile.Tag = null;
                AppsPanel.Children.Add(tile);
                _visible.Add(tile);
            }

            if (searching)
            {
                var web = MakeTile($"„{q}“ im Web suchen",
                    () => AppCatalog.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(q)),
                    img => img.Source = WebIcon());
                AppsPanel.Children.Add(web);
                _visible.Add(web);
            }

            EmptyText.Visibility = searching && _visible.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
            Select(searching ? 0 : -1);
        }

        private static int Score(string name, string q)
        {
            if (name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 3;
            foreach (var word in name.Split(' ', '-', '.', '_'))
                if (word.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 2;
            return name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ? 1 : 0;
        }

        private static ImageSource WebIcon()
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)), null,
                new RectangleGeometry(new Rect(0, 0, 100, 100), 22, 22)));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 6);
            g.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(50, 50), 28, 28)));
            g.Children.Add(new GeometryDrawing(null, pen, new EllipseGeometry(new Point(50, 50), 12, 28)));
            g.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(22, 50), new Point(78, 50))));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        private void Select(int index)
        {
            if (_selected >= 0 && _selected < _visible.Count) _visible[_selected].Tag = null;
            _selected = _visible.Count == 0 ? -1 : Math.Clamp(index, -1, _visible.Count - 1);
            if (_selected >= 0)
            {
                _visible[_selected].Tag = "selected";
                _visible[_selected].BringIntoView();
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            int cols = Math.Max(1, (int)(AppsPanel.ActualWidth / 112));
            switch (e.Key)
            {
                case Key.Escape:
                    HideMenu();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    int i = _selected >= 0 ? _selected : 0;
                    if (i < _visible.Count)
                        _visible[i].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    e.Handled = true;
                    break;
                case Key.Down:
                    Select(_selected < 0 ? 0 : _selected + cols);
                    e.Handled = true;
                    break;
                case Key.Up:
                    Select(Math.Max(0, _selected - cols));
                    e.Handled = true;
                    break;
                case Key.Right when SearchBox.CaretIndex == SearchBox.Text.Length:
                    Select(_selected + 1);
                    e.Handled = true;
                    break;
                case Key.Left when _selected > 0 && SearchBox.CaretIndex == SearchBox.Text.Length:
                    Select(_selected - 1);
                    e.Handled = true;
                    break;
            }
        }

        // ---------------------------------------------------------------- Ein/Aus

        private void Power_Click(object sender, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            m.Items.Add(TopBar.Item("Sperren", () => { HideMenu(); Power.Lock(); }));
            m.Items.Add(TopBar.Item("Energie sparen", () => { HideMenu(); Power.Sleep(); }));
            m.Items.Add(new Separator());
            m.Items.Add(TopBar.Item("Neu starten …", () => { HideMenu(); Power.Restart(); }));
            m.Items.Add(TopBar.Item("Herunterfahren …", () => { HideMenu(); Power.Shutdown(); }));
            m.Items.Add(TopBar.Item("Abmelden …", () => { HideMenu(); Power.SignOut(); }));
            m.PlacementTarget = PowerButton;
            m.Placement = PlacementMode.Top;
            OpenSubMenu(m);
        }
    }
}
