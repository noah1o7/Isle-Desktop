using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Isle
{
    public partial class Dock : Window
    {
        private const double IconSize = 50;
        private const double TileMargin = 3;
        private const double Slot = IconSize + 2 * TileMargin;
        private const double MaxScale = 1.55;
        private const double ReserveHeight = 78;   // so viel Platz wird unten für andere Fenster gesperrt
        private const double WindowHeight = 170;   // Platz nach oben für vergrößerte Icons, Namensblase, Ziehen
        private const double SideRoom = 130;       // Platz links/rechts für die Vergrößerung
        private const double DragThreshold = 6;
        private const double RemoveDistance = 60;  // so weit nach oben ziehen = aus dem Dock entfernen


        private readonly List<DockTile> _tiles = new();
        // Nur noch als Sicherheitsnetz – normalerweise meldet Windows Änderungen selbst (WinEvents)
        private readonly DispatcherTimer _fallback = new() { Interval = TimeSpan.FromSeconds(3) };
        private List<RunningApp> _running = new();
        private List<string> _unpinnedKeys = new();
        private string _signature = "";
        private AppBar? _appBar;
        private IntPtr _hwnd;
        private bool _fullscreen;

        // Vergrößerung: höchstens einmal pro Bildschirm-Frame berechnen (Gaming-Mäuse melden bis zu 1000×/s)
        private double? _mouseX;
        private bool _magnifyDirty;
        private bool _renderHooked;
        private DockTile? _hovered;

        // Klicken & Ziehen
        private DockTile? _press;
        private Point _pressPoint;
        private bool _dragging;
        private bool _removeMode;
        private double _grabOffset;

        // Fenster-Vorschau
        private readonly WindowPreview _preview = new();
        private readonly DispatcherTimer _pvDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _pvPoll = new() { Interval = TimeSpan.FromMilliseconds(120) };
        private DockTile? _pvTarget;
        private DockTile? _pvTile;
        private int _pvMiss;

        public Dock()
        {
            InitializeComponent();
            _fallback.Tick += (_, _) => Refresh();
            _pvDelay.Tick += (_, _) =>
            {
                _pvDelay.Stop();
                if (_pvTarget != null && _hovered == _pvTarget && !_dragging) ShowPreview(_pvTarget);
            };
            _pvPoll.Tick += (_, _) => PollPreview();
            _preview.WindowActivated += HidePreview;

            AppCatalog.Loaded += () =>
            {
                EnsureDefaultPins();
                // Einmalig: Apps nachholen, die in deiner Windows-Taskleiste sind, aber im Dock fehlen
                if (!Settings.Current.TaskbarImported) ImportTaskbarPins();
                Refresh(force: true);
            };
            Loaded += (_, _) =>
            {
                Refresh(force: true);
                _fallback.Start();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeToolWindow(_hwnd);
            _appBar = new AppBar(this, Win32.ABE_BOTTOM, ReserveHeight, WindowHeight);
            _appBar.FullscreenChanged += fs =>
            {
                _fullscreen = fs;
                if (fs) HidePreview();
                Visibility = fs ? Visibility.Hidden : Visibility.Visible;
            };
            _appBar.Register();
            UpdateWindowWidth();
        }

        public void ShowDock()
        {
            Show();
            _appBar?.Register();
            UpdateWindowWidth();
            _fallback.Start();
            Refresh(force: true);
        }

        public void ReregisterAppBar()
        {
            _appBar?.Unregister();
            _appBar?.Register();
            UpdateWindowWidth();
        }

        public void HideDock()
        {
            _fallback.Stop();
            HidePreview();
            _appBar?.Unregister();
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            _fallback.Stop();
            _preview.Close();
            _appBar?.Dispose();
            base.OnClosed(e);
        }

        // ---------------------------------------------------------------- Pins

        private void EnsureDefaultPins()
        {
            var s = Settings.Current;
            if (s.PinsInitialized || AppCatalog.Apps.Count == 0) return;

            // Erster Start: genau die Apps aus deiner Windows-Taskleiste übernehmen
            var fromTaskbar = TaskbarPins.Read();
            if (fromTaskbar.Count > 0)
            {
                foreach (var p in fromTaskbar)
                    if (!TaskbarPins.AlreadyPinned(p, s.Pins)) s.Pins.Add(p);
                s.PinsInitialized = true;
                s.TaskbarImported = true;
                Settings.Save();
                return;
            }

            var candidates = new[]
            {
                AppCatalog.FindById("Microsoft.Windows.Explorer") ?? AppCatalog.FindByName("Datei-Explorer", "File Explorer"),
                AppCatalog.FindByName("Google Chrome", "Microsoft Edge", "Firefox", "Brave", "Opera"),
                AppCatalog.FindByName("Spotify"),
                AppCatalog.FindByName("Discord"),
                AppCatalog.FindByName("Steam"),
                AppCatalog.FindByName("Einstellungen", "Settings"),
            };
            foreach (var app in candidates)
                if (app != null && !IsPinned(app.Id))
                    s.Pins.Add(new PinnedApp { Id = app.Id, Name = app.Name, ExePath = app.ExePath });

            s.PinsInitialized = true;
            Settings.Save();
        }

        /// <summary>
        /// Holt alle Apps aus der Windows-Taskleiste, die im Dock noch fehlen, und hängt sie hinten an.
        /// Gibt zurück, wie viele dazugekommen sind.
        /// </summary>
        public int ImportTaskbarPins()
        {
            var s = Settings.Current;
            int added = 0;
            foreach (var p in TaskbarPins.Read())
            {
                if (TaskbarPins.AlreadyPinned(p, s.Pins)) continue;
                s.Pins.Add(p);
                added++;
            }
            s.TaskbarImported = true;
            Settings.Save();
            if (added > 0) Refresh(force: true);
            return added;
        }

        public bool IsPinned(string id) =>
            Settings.Current.Pins.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        public void Pin(AppEntry app) => AddPin(new PinnedApp { Id = app.Id, Name = app.Name, ExePath = app.ExePath });

        private void AddPin(PinnedApp pin, int? index = null)
        {
            if (pin.Id.Length == 0 || IsPinned(pin.Id)) return;
            var pins = Settings.Current.Pins;
            pins.Insert(Math.Clamp(index ?? pins.Count, 0, pins.Count), pin);
            Settings.Save();
            Refresh(force: true);
        }

        public void Unpin(string id)
        {
            Settings.Current.Pins.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            Settings.Save();
            Refresh(force: true);
        }

        private void PinRunning(RunningApp app) =>
            AddPin(new PinnedApp { Id = app.AppId ?? app.ExePath ?? "", Name = app.Name, ExePath = app.ExePath });

        // ---------------------------------------------------------------- Aufbau

        private static string Signature(IEnumerable<PinnedApp> pins, IEnumerable<string> unpinnedKeys) =>
            string.Join("|", pins.Select(p => p.Id)) + "#" + string.Join("|", unpinnedKeys);

        /// <summary>Wird von WinEvents bei Fensteränderungen aufgerufen (und alle 3 s als Sicherheitsnetz).</summary>
        public void Refresh(bool force = false)
        {
            if (!IsVisible && !force) return;
            if (_dragging || _press != null) return; // nicht unter der Maus umbauen

            try { _running = WindowTracker.Snapshot(); } catch { _running = new(); }
            var pins = Settings.Current.Pins;

            var pinMatches = pins.Select(p => _running.FirstOrDefault(r => WindowTracker.Matches(p, r))).ToList();
            var unpinned = _running.Where(r => !pins.Any(p => WindowTracker.Matches(p, r))).ToList();
            _unpinnedKeys = unpinned.Select(r => r.Key).ToList();

            string sig = Signature(pins, _unpinnedKeys);
            if (force || sig != _signature || _tiles.Count == 0)
            {
                _signature = sig;
                Rebuild(pinMatches, unpinned);
            }
            else
            {
                // Nur Laufend-Punkte aktualisieren
                int pi = 0, ui = 0;
                foreach (var t in _tiles)
                {
                    if (t.Kind == TileKind.Pinned && pi < pinMatches.Count) t.SetRunning(pinMatches[pi++]);
                    else if (t.Kind == TileKind.Running && ui < unpinned.Count) t.SetRunning(unpinned[ui++]);
                }
            }

            if (!_fullscreen && IsVisible && !TaskbarControl.IsRevealed()) NativeMethods.KeepOnTop(_hwnd);
        }

        private void Rebuild(List<RunningApp?> pinMatches, List<RunningApp> unpinned)
        {
            HidePreview();
            Items.Children.Clear();
            _tiles.Clear();
            _hovered = null;
            NameBubble.Visibility = Visibility.Collapsed;

            var start = new DockTile(TileKind.Start, "Start", IconSize) { OnClick = () => ((App)Application.Current).ToggleStartMenu() };
            start.SetImage(StartIcon());
            AddTile(start);

            var pins = Settings.Current.Pins;
            for (int i = 0; i < pins.Count; i++)
            {
                var pin = pins[i];
                var tile = new DockTile(TileKind.Pinned, pin.Name, IconSize) { Pin = pin };
                tile.SetRunning(pinMatches.ElementAtOrDefault(i));
                tile.OnClick = () =>
                {
                    if (tile.Running != null) WindowTracker.Activate(tile.Running);
                    else { tile.BounceOnce(); AppCatalog.Launch(pin.Id, pin.ExePath); }
                };
                if (System.IO.Path.IsPathRooted(pin.Id))
                    ShellIcons.Get(96, true, img => tile.SetImage(img), pin.Id, pin.ExePath);
                else
                    ShellIcons.Get(96, true, img => tile.SetImage(img), @"shell:AppsFolder\" + pin.Id, pin.ExePath, pin.Id);
                AddTile(tile);
            }

            if (unpinned.Count > 0)
            {
                AddTile(new DockTile(TileKind.Separator, "", IconSize));
                foreach (var r in unpinned)
                {
                    var tile = new DockTile(TileKind.Running, r.Name, IconSize);
                    tile.SetRunning(r);
                    tile.OnClick = () => { if (tile.Running != null) WindowTracker.Activate(tile.Running); };
                    ShellIcons.Get(96, true, img => tile.SetImage(img),
                        r.AppId != null ? @"shell:AppsFolder\" + r.AppId : null, r.ExePath);
                    AddTile(tile);
                }
            }

            UpdateWindowWidth();
            if (_mouseX != null) RequestFrame();
        }

        private void AddTile(DockTile tile)
        {
            tile.Margin = new Thickness(TileMargin, 0, TileMargin, 0);
            _tiles.Add(tile);
            Items.Children.Add(tile);
        }

        /// <summary>Fenster nur so breit wie nötig – spart bei jeder Animation viel Zeichenarbeit.</summary>
        private void UpdateWindowWidth()
        {
            if (_appBar == null) return;
            double dockWidth = _tiles.Sum(t => t.BaseWidth + 2 * TileMargin) + 16;
            _appBar.WindowWidthDip = dockWidth + 2 * SideRoom;
            _appBar.PlaceWindow();
        }

        private static ImageSource StartIcon()
        {
            var g = new DrawingGroup();
            var bg = new LinearGradientBrush(Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0x0A, 0x84, 0xFF), 45);
            g.Children.Add(new GeometryDrawing(bg, null, new RectangleGeometry(new Rect(0, 0, 100, 100), 22, 22)));
            var dot = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    g.Children.Add(new GeometryDrawing(dot, null,
                        new RectangleGeometry(new Rect(22 + x * 21, 22 + y * 21, 14, 14), 4, 4)));
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }

        private static DockTile? TileFrom(object? source)
        {
            var d = source as DependencyObject;
            while (d != null)
            {
                if (d is DockTile t) return t;
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private DockTile? TileAt(Point posInItems)
        {
            var hit = VisualTreeHelper.HitTest(Items, posInItems);
            return TileFrom(hit?.VisualHit);
        }

        // ---------------------------------------------------------------- Maus: Klicken, Ziehen, Vergrößern

        private void Area_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var tile = TileFrom(e.OriginalSource);
            if (tile == null || tile.Kind == TileKind.Separator) return;
            HidePreview();
            _press = tile;
            _pressPoint = e.GetPosition(Items);
            tile.SetPressed(true);
            DockArea.CaptureMouse();
            e.Handled = true;
        }

        private void Area_MouseMove(object sender, MouseEventArgs e)
        {
            if (_press != null && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(Items);
                if (!_dragging && _press.Kind == TileKind.Pinned &&
                    (Math.Abs(pos.X - _pressPoint.X) > DragThreshold || Math.Abs(pos.Y - _pressPoint.Y) > DragThreshold))
                    StartDrag();
                if (_dragging) DragTo(pos);
                return;
            }

            _mouseX = e.GetPosition(Items).X;
            RequestFrame();
        }

        private void Area_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var tile = _press;
            if (tile == null) return;
            _press = null;
            tile.SetPressed(false);
            e.Handled = true;

            if (_dragging)
            {
                EndDrag(tile);
            }
            else if (TileAt(e.GetPosition(Items)) == tile)
            {
                tile.OnClick?.Invoke();
            }
            DockArea.ReleaseMouseCapture();
        }

        private void Area_LostCapture(object sender, MouseEventArgs e)
        {
            // z. B. Alt+Tab mitten im Ziehen
            if (_press == null) return;
            var tile = _press;
            _press = null;
            tile.SetPressed(false);
            if (_dragging) { _removeMode = false; EndDrag(tile); }
        }

        private void Area_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_press != null || _dragging) return;
            _mouseX = null;
            foreach (var t in _tiles) t.SetScale(1, animate: true);
            NameBubble.Visibility = Visibility.Collapsed;
            SetHovered(null);
        }

        private void Area_RightUp(object sender, MouseButtonEventArgs e)
        {
            var tile = TileFrom(e.OriginalSource);
            if (tile == null || tile.Kind == TileKind.Separator) return;
            e.Handled = true;
            ShowTileMenu(tile);
        }

        private void RequestFrame()
        {
            _magnifyDirty = true;
            if (_renderHooked) return;
            _renderHooked = true;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_magnifyDirty)
            {
                CompositionTarget.Rendering -= OnRendering; // nichts zu tun → nicht jeden Frame mitlaufen
                _renderHooked = false;
                return;
            }
            _magnifyDirty = false;
            ApplyMagnify();
        }

        private void ApplyMagnify()
        {
            if (_mouseX is not double mx || _tiles.Count == 0 || Items.ActualWidth <= 0 || _dragging) return;

            double baseTotal = _tiles.Sum(t => t.BaseWidth + 2 * TileMargin);
            double x = mx / Items.ActualWidth * baseTotal;
            double radius = Slot * 2.6;

            DockTile? hovered = null;
            double left = 0;
            foreach (var t in _tiles)
            {
                double w = t.BaseWidth + 2 * TileMargin;
                if (t.Kind != TileKind.Separator)
                {
                    double d = Math.Abs(x - (left + w / 2)) / radius;
                    t.SetScale(1 + (MaxScale - 1) * Math.Max(0, 1 - d * d), animate: false);
                    if (x >= left && x < left + w) hovered = t;
                }
                left += w;
            }

            if (_preview.IsVisible) NameBubble.Visibility = Visibility.Collapsed;
            else ShowBubble(hovered, hovered?.Title);
            SetHovered(hovered);
        }

        private void ShowBubble(DockTile? tile, string? text)
        {
            if (tile == null || string.IsNullOrEmpty(text))
            {
                NameBubble.Visibility = Visibility.Collapsed;
                return;
            }
            if (NameBubbleText.Text != text) NameBubbleText.Text = text;
            NameBubble.Visibility = Visibility.Visible;
            Root.UpdateLayout();
            var size = NameBubble.DesiredSize;
            var pt = tile.TranslatePoint(new Point(tile.ActualWidth / 2, 0), Root);
            Canvas.SetLeft(NameBubble, Math.Max(4, pt.X - size.Width / 2));
            Canvas.SetTop(NameBubble, Math.Max(0, pt.Y - size.Height - 8));
        }

        // ---------------------------------------------------------------- Icons verschieben

        private (int first, int last) PinnedRange()
        {
            int first = _tiles.FindIndex(t => t.Kind == TileKind.Pinned);
            int last = _tiles.FindLastIndex(t => t.Kind == TileKind.Pinned);
            return (first, last);
        }

        private static double SlotLeft(UIElement e) => LayoutInformation.GetLayoutSlot((FrameworkElement)e).X;

        private void StartDrag()
        {
            if (_press == null) return;
            _dragging = true;
            _removeMode = false;
            _mouseX = null;
            HidePreview();
            NameBubble.Visibility = Visibility.Collapsed;
            foreach (var t in _tiles) t.SetScale(1, animate: false); // beim Ziehen alle in Normalgröße
            Items.UpdateLayout();
            // Icon mittig unter die Maus – das Dock ist eben geschrumpft, die alte Klickposition passt nicht mehr
            _grabOffset = Slot / 2;
            Panel.SetZIndex(_press, 10);
        }

        private void DragTo(Point pos)
        {
            var tile = _press!;
            var (first, last) = PinnedRange();
            if (first < 0) return;

            double minLeft = SlotLeft(_tiles[first]);
            double maxLeft = SlotLeft(_tiles[last]);
            double desired = Math.Clamp(pos.X - _grabOffset, minLeft, maxLeft);

            int cur = _tiles.IndexOf(tile);
            int target = Math.Clamp(first + (int)Math.Round((desired - minLeft) / Slot), first, last);
            if (target != cur)
            {
                // Nachbarn gleiten sanft in ihre neue Lücke
                if (target > cur) for (int j = cur + 1; j <= target; j++) _tiles[j].SlideFrom(Slot);
                else for (int j = target; j < cur; j++) _tiles[j].SlideFrom(-Slot);

                _tiles.RemoveAt(cur);
                _tiles.Insert(target, tile);
                Items.Children.Remove(tile);
                Items.Children.Insert(target, tile);

                var pins = Settings.Current.Pins;
                var pin = pins[cur - first];
                pins.RemoveAt(cur - first);
                pins.Insert(target - first, pin);
                Items.UpdateLayout();
            }

            // Nach oben aus dem Dock ziehen = entfernen
            double bgTop = DockBg.TranslatePoint(new Point(0, 0), Items).Y;
            _removeMode = pos.Y < bgTop - RemoveDistance;
            var slot = LayoutInformation.GetLayoutSlot(tile);
            double dy = Math.Clamp(pos.Y - (slot.Y + slot.Height / 2), -95, -6);

            tile.SetDragOffset(desired - SlotLeft(tile), dy);
            tile.SetRemoveHint(_removeMode);
            ShowBubble(tile, _removeMode ? "Entfernen" : null);
        }

        private void EndDrag(DockTile tile)
        {
            _dragging = false;
            Panel.SetZIndex(tile, 0);
            NameBubble.Visibility = Visibility.Collapsed;

            if (_removeMode && tile.Pin != null)
            {
                _removeMode = false;
                tile.Poof(() => Unpin(tile.Pin.Id));
                return;
            }

            _removeMode = false;
            tile.SetRemoveHint(false);
            tile.AnimateHome();
            Settings.Save();
            _signature = Signature(Settings.Current.Pins, _unpinnedKeys); // neue Reihenfolge ist schon sichtbar
        }

        // ---------------------------------------------------------------- Dateien aufs Dock ziehen = anheften

        private void Area_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        }

        private void Area_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

            // Einfügen an der Stelle, wo losgelassen wurde
            var (first, last) = PinnedRange();
            int index = Settings.Current.Pins.Count;
            if (first >= 0)
            {
                double x = e.GetPosition(Items).X;
                index = Math.Clamp((int)Math.Round((x - SlotLeft(_tiles[first])) / Slot), 0, last - first + 1);
            }

            foreach (var path in paths)
            {
                string? exe = null;
                string name;
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    exe = path;
                    name = WindowTracker.Describe(path) ?? System.IO.Path.GetFileNameWithoutExtension(path);
                }
                else
                {
                    if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) exe = AppCatalog.ShortcutTarget(path);
                    name = Directory.Exists(path) ? new DirectoryInfo(path).Name : System.IO.Path.GetFileNameWithoutExtension(path);
                }
                AddPin(new PinnedApp { Id = path, Name = name, ExePath = exe }, index++);
            }
            e.Handled = true;
        }

        // ---------------------------------------------------------------- Fenster-Vorschau

        private void SetHovered(DockTile? tile)
        {
            if (tile == _hovered) return;
            _hovered = tile;
            if (_dragging || _press != null) return;

            if (tile?.Running == null || tile.Kind is not (TileKind.Pinned or TileKind.Running))
            {
                _pvDelay.Stop();
                return;
            }
            if (_preview.IsVisible)
            {
                ShowPreview(tile); // schon offen: sofort zur neuen App wechseln
            }
            else
            {
                _pvTarget = tile;
                _pvDelay.Stop();
                _pvDelay.Start();
            }
        }

        private void ShowPreview(DockTile tile)
        {
            if (tile.Running == null || PresentationSource.FromVisual(tile) == null || _fullscreen) return;
            string key = tile.Running.Key;
            var dpi = VisualTreeHelper.GetDpi(this);
            var top = tile.PointToScreen(new Point(tile.ActualWidth / 2, 0));
            double bgTop = DockBg.PointToScreen(new Point(0, 0)).Y;

            bool ok = _preview.ShowFor(key,
                () => _running.FirstOrDefault(r => r.Key == key),
                tile.Image,
                top.X / dpi.DpiScaleX,
                bgTop / dpi.DpiScaleY - 10);

            if (!ok) return;
            NameBubble.Visibility = Visibility.Collapsed;
            _pvTile = tile;
            _pvMiss = 0;
            _pvPoll.Start();
        }

        /// <summary>Schließt die Vorschau, sobald die Maus weder auf dem Icon noch auf der Vorschau ist.</summary>
        private void PollPreview()
        {
            if (!_preview.IsVisible) { _pvPoll.Stop(); return; }

            bool overIcon = false;
            if (_pvTile != null && PresentationSource.FromVisual(_pvTile) != null)
            {
                var a = _pvTile.PointToScreen(new Point(0, 0));
                var b = _pvTile.PointToScreen(new Point(_pvTile.ActualWidth, _pvTile.ActualHeight));
                overIcon = Win32.CursorIn(new Win32.RECT { Left = (int)a.X, Top = (int)a.Y - 30, Right = (int)b.X, Bottom = (int)b.Y });
            }

            if (overIcon || _preview.IsCursorOver()) { _pvMiss = 0; return; }
            if (++_pvMiss >= 3) HidePreview();
        }

        private void HidePreview()
        {
            _pvDelay.Stop();
            _pvPoll.Stop();
            _pvTile = null;
            _preview.HidePreview();
        }

        // ---------------------------------------------------------------- Kontextmenü

        private void ShowTileMenu(DockTile tile)
        {
            HidePreview();
            NameBubble.Visibility = Visibility.Collapsed;

            var m = new ContextMenu();
            var running = tile.Running;
            var windows = running != null ? WindowTracker.LiveWindows(running) : new List<IntPtr>();

            switch (tile.Kind)
            {
                case TileKind.Pinned:
                case TileKind.Running:
                    for (int i = 0; i < windows.Count && i < 10; i++)
                    {
                        IntPtr h = windows[i];
                        string title = Win32.WindowTitle(h);
                        if (title.Length > 60) title = title[..57] + "…";
                        m.Items.Add(TopBar.Item(title, () => Win32.ForceForeground(h)));
                    }
                    if (windows.Count > 0) m.Items.Add(new Separator());

                    if (tile.Pin is PinnedApp pin)
                    {
                        m.Items.Add(TopBar.Item(running != null ? "Neues Fenster" : "Öffnen",
                            () => { tile.BounceOnce(); AppCatalog.Launch(pin.Id, pin.ExePath); }));
                        m.Items.Add(TopBar.Item("Aus dem Dock entfernen", () => Unpin(pin.Id)));
                    }
                    else if (running != null)
                    {
                        m.Items.Add(TopBar.Item("Im Dock behalten", () => PinRunning(running)));
                    }

                    if (running != null && windows.Count > 0)
                    {
                        m.Items.Add(new Separator());
                        m.Items.Add(TopBar.Item(windows.Count == 1 ? "Fenster schließen" : $"Alle {windows.Count} Fenster schließen",
                            () => WindowTracker.CloseAll(running)));
                        if (WindowTracker.CanKill(running))
                            m.Items.Add(TopBar.Item("Task beenden", () => WindowTracker.Kill(running)));
                    }
                    break;

                case TileKind.Start:
                    m.Items.Add(TopBar.Item("Isle-Start öffnen", () => ((App)Application.Current).ToggleStartMenu()));
                    break;
            }

            if (m.Items.Count == 0) return;
            Menus.Open(m, tile, PlacementMode.Top, -8);
        }
    }

    internal enum TileKind { Start, Pinned, Running, Separator }

    /// <summary>Ein Platz im Dock: Icon + Laufend-Punkt.</summary>
    internal sealed class DockTile : StackPanel
    {
        private static readonly Duration Quick = TimeSpan.FromMilliseconds(170);

        private readonly Image _img = new() { Stretch = Stretch.Uniform };
        private readonly Ellipse _dot = new() { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)), Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Hidden };
        private readonly TranslateTransform _bounce = new();
        private readonly TranslateTransform _shift = new(); // Verschieben/Gleiten
        private readonly double _base;
        private double _scale = 1;

        public TileKind Kind { get; }
        public string Title { get; }
        public PinnedApp? Pin { get; init; }
        public RunningApp? Running { get; private set; }
        public Action? OnClick { get; set; }
        public ImageSource? Image => _img.Source;
        public double BaseWidth => Kind == TileKind.Separator ? 7 : _base;

        public DockTile(TileKind kind, string title, double baseSize)
        {
            Kind = kind;
            Title = title;
            _base = baseSize;
            Orientation = Orientation.Vertical;
            VerticalAlignment = VerticalAlignment.Bottom;
            Background = Brushes.Transparent;
            RenderTransform = _shift;

            if (kind == TileKind.Separator)
            {
                Children.Add(new Border
                {
                    Width = 1, Height = baseSize - 6, Margin = new Thickness(3, 0, 3, 10),
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                });
                return;
            }

            _img.Width = _img.Height = baseSize;
            _img.RenderTransform = _bounce;
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
            Children.Add(_img);
            Children.Add(_dot);
            Cursor = Cursors.Hand;
        }

        public void SetImage(ImageSource? img) => _img.Source = img;

        public void SetRunning(RunningApp? app)
        {
            Running = app;
            var v = app != null ? Visibility.Visible : Visibility.Hidden;
            if (_dot.Visibility != v) _dot.Visibility = v;
        }

        public void SetPressed(bool pressed) => _img.Opacity = pressed ? 0.65 : 1;

        public void SetScale(double scale, bool animate)
        {
            if (Kind == TileKind.Separator) return;
            double size = _base * scale;
            if (animate)
            {
                if (Math.Abs(_scale - scale) < 0.001) return;
                _scale = scale;
                var a = new DoubleAnimation(size, Quick) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                _img.BeginAnimation(WidthProperty, a);
                _img.BeginAnimation(HeightProperty, a);
            }
            else
            {
                // Winzige Änderungen auslassen → weniger Layout-Arbeit pro Frame
                if (Math.Abs(_scale - scale) * _base < 0.25 && !_img.HasAnimatedProperties) return;
                _scale = scale;
                _img.BeginAnimation(WidthProperty, null);
                _img.BeginAnimation(HeightProperty, null);
                _img.Width = _img.Height = size;
            }
        }

        /// <summary>Nachbar-Icon ist eben verrutscht: von der alten Stelle sanft hingleiten.</summary>
        public void SlideFrom(double dx)
        {
            double from = _shift.X + dx;
            _shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(from, 0, Quick) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        public void SetDragOffset(double x, double y)
        {
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.BeginAnimation(TranslateTransform.YProperty, null);
            _shift.X = x;
            _shift.Y = y;
        }

        public void AnimateHome()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_shift.X, 0, Quick) { EasingFunction = ease });
            _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_shift.Y, 0, Quick) { EasingFunction = ease });
            _shift.X = 0;
            _shift.Y = 0;
        }

        public void SetRemoveHint(bool remove) => Opacity = remove ? 0.45 : 1;

        /// <summary>Kurz größer werden und verschwinden – dann entfernen.</summary>
        public void Poof(Action done)
        {
            var st = new ScaleTransform(1, 1, _base / 2, _base / 2);
            _img.RenderTransform = st;
            var d = TimeSpan.FromMilliseconds(220);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.4, d));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.4, d));
            var fade = new DoubleAnimation(0, d);
            fade.Completed += (_, _) => done();
            BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Hüpfen beim Starten einer App – wie beim Mac.</summary>
        public void BounceOnce()
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1000) };
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), ease));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(440)), easeIn));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640)), ease));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(840)), easeIn));
            _bounce.BeginAnimation(TranslateTransform.YProperty, a);
        }
    }
}
