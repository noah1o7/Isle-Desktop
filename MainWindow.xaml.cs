using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Isle
{
    public partial class MainWindow : Window
    {
        private enum IslandState { Idle, Compact, Peek, Expanded }
        private enum Page { Music, Devices, Reminders }

        // Größen der Zustände (Breite, Höhe) in geräteunabhängigen Pixeln
        private static readonly Size IdleSize = new(96, 24);
        private static readonly Size CompactSize = new(224, 34);
        private static readonly Size PeekSize = new(340, 40);
        private static readonly Size ExpandedSize = new(360, 236); // Verhältnis wie in der Vorlage
        private const double MaxRadius = 34;

        private static readonly Geometry PlayGeometry = Geometry.Parse("M1,0 L1,14 L13,7 Z");
        private static readonly Geometry PauseGeometry = Geometry.Parse("M0,0 H3.5 V14 H0 Z M8.5,0 H12 V14 H8.5 Z");
        private static readonly Geometry SpeakerOn = Geometry.Parse("M0,5 H3 L7,1 V15 L3,11 H0 Z M10,4.5 A5,5 0 0 1 10,11.5 M12.5,2 A8.5,8.5 0 0 1 12.5,14");
        private static readonly Geometry SpeakerOff = Geometry.Parse("M0,5 H3 L7,1 V15 L3,11 H0 Z M10,5 L15,11 M15,5 L10,11");
        private static readonly Brush Primary = (Brush)Application.Current.Resources["TextPrimary"];
        private static readonly Brush Secondary = (Brush)Application.Current.Resources["TextSecondary"];
        private static readonly Brush FocusPurple = new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2));

        private const int HotkeyQuitId = 0x4953;
        private const int WS_EX_TRANSPARENT = 0x20;

        private readonly MediaService _media = new();
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _hitTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
        private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
        private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };

        private readonly EqualizerBars _compactBars = new(13);
        private readonly EqualizerBars _peekBars = new(14);
        private readonly EqualizerBars _expandedBars = new(14);

        private IslandState _state = IslandState.Idle;
        private Page _page = Page.Music;
        private MediaSnapshot? _snap;
        private BitmapSource? _art;
        private string _lastTrackKey = "";
        private string _lastThumbKey = "";
        private bool _firstSnapshot = true;
        private bool _hovering;
        private bool _inside;
        private bool _clickThrough;
        private bool _suppressHover;
        private bool _notice;
        private bool _seeking;
        private double _seekFraction;
        private bool _volDragging;
        private bool _hiddenForFullscreen;
        private int _tickCount;
        private IntPtr _hwnd;

        public MainWindow()
        {
            InitializeComponent();

            CompactBarsHost.Content = _compactBars;
            PeekBarsHost.Content = _peekBars;
            ExpandedBarsHost.Content = _expandedBars;
            PlayIcon.Data = PlayGeometry;
            BuildPageDots();
            UpdateFocusVisuals();

            _tick.Tick += (_, _) => OnTick();
            _hitTimer.Tick += (_, _) => OnHitTest();
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (!KeepOpen())
                {
                    _hovering = false;
                    DisableTyping();
                    ApplyState(RestingState());
                }
            };
            _peekTimer.Tick += (_, _) =>
            {
                _peekTimer.Stop();
                EndNotice();
                if (!_hovering) ApplyState(RestingState());
            };

            _media.Changed += snap => Dispatcher.BeginInvoke(() => OnMediaChanged(snap));
            Deactivated += (_, _) => { DisableTyping(); if (!_inside) _collapseTimer.Start(); };

            Loaded += async (_, _) =>
            {
                PositionWindow();
                UpdateFooterDate();
                _tick.Start();
                _hitTimer.Start();
                try { await _media.InitializeAsync(); }
                catch
                {
                    ExpandedTitle.Text = "Mediensteuerung nicht verfügbar";
                }
            };

            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(PositionWindow);
            };
        }

        // ---------------------------------------------------------------- Fenster-Setup

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeToolWindow(_hwnd); // nicht in Alt+Tab, stiehlt keinen Fokus
            SetClickThrough(true);

            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
            // Strg + Alt + Q beendet Isle
            NativeMethods.RegisterHotKey(_hwnd, HotkeyQuitId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.Q));
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyQuitId)
            {
                handled = true;
                ((App)Application.Current).Quit();
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(_hwnd, HotkeyQuitId);
            _tick.Stop();
            _hitTimer.Stop();
            _media.Dispose();
            base.OnClosed(e);
        }

        private void PositionWindow()
        {
            // Ganz oben am Bildschirmrand, mittig – sitzt damit auf der Top-Bar
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        }

        // ---------------------------------------------------------------- Klickbarer Bereich = nur die Insel

        /// <summary>
        /// Prüft ~30× pro Sekunde, ob die Maus wirklich auf der Insel ist. Nur dann nimmt das Fenster Klicks an –
        /// sonst ist es komplett durchlässig, auch im Schatten und im durchsichtigen Rand darum herum.
        /// </summary>
        private void OnHitTest()
        {
            if (!IsVisible || _hwnd == IntPtr.Zero || PresentationSource.FromVisual(Island) == null) return;

            var a = Island.PointToScreen(new Point(0, 0));
            var b = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
            bool inside = Win32.CursorIn(new Win32.RECT { Left = (int)a.X, Top = (int)a.Y, Right = (int)b.X, Bottom = (int)b.Y });

            SetClickThrough(!(inside || _seeking || _volDragging || ReminderInput.IsKeyboardFocused));

            if (inside == _inside) return;
            _inside = inside;
            if (inside) OnEnter(); else OnLeave();
        }

        private void SetClickThrough(bool through)
        {
            if (through == _clickThrough || _hwnd == IntPtr.Zero) return;
            _clickThrough = through;
            long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex = through ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        }

        private bool KeepOpen() =>
            _inside || _seeking || _volDragging || ReminderInput.IsKeyboardFocused;

        private void OnEnter()
        {
            if (_suppressHover) return;
            _collapseTimer.Stop();
            _peekTimer.Stop();
            EndNotice();
            _hovering = true;
            ApplyState(IslandState.Expanded);
        }

        private void OnLeave()
        {
            _suppressHover = false;
            if (KeepOpen()) return;
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        // ---------------------------------------------------------------- Zustände & Animation

        private IslandState RestingState() =>
            _snap != null && !string.IsNullOrEmpty(_snap.Title) ? IslandState.Compact : IslandState.Idle;

        private void ApplyState(IslandState state, bool force = false)
        {
            if (state == _state && !force) return;
            bool growing = SizeOf(state).Width * SizeOf(state).Height > SizeOf(_state).Width * SizeOf(_state).Height;
            _state = state;
            UpdateBars();
            if (state == IslandState.Expanded)
            {
                UpdateFooterDate();
                Dispatcher.BeginInvoke(new Action(() => { UpdateProgress(); RefreshPage(); }), DispatcherPriority.Loaded);
            }

            var size = SizeOf(state);
            IEasingFunction ease = growing
                ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(growing ? 420 : 300);

            Island.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, dur) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height, dur) { EasingFunction = ease });

            FadePanel(IdlePanel, state == IslandState.Idle);
            FadePanel(CompactPanel, state == IslandState.Compact);
            FadePanel(PeekPanel, state == IslandState.Peek);
            FadePanel(ExpandedPanel, state == IslandState.Expanded);
        }

        private static Size SizeOf(IslandState s) => s switch
        {
            IslandState.Compact => CompactSize,
            IslandState.Peek => PeekSize,
            IslandState.Expanded => ExpandedSize,
            _ => IdleSize,
        };

        private static void FadePanel(UIElement panel, bool visible, double delayMs = 110)
        {
            panel.IsHitTestVisible = visible;
            var anim = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 240 : 110))
            {
                // Inhalt erst einblenden, wenn die Insel schon etwas gewachsen ist
                BeginTime = visible ? TimeSpan.FromMilliseconds(delayMs) : TimeSpan.Zero,
            };
            panel.BeginAnimation(OpacityProperty, anim);
        }

        private void Island_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Ecken-Radius folgt der Höhe → wirkt wie eine echte Pille, die zur Karte wird
            double w = Island.ActualWidth, h = Island.ActualHeight;
            double r = Math.Min(h / 2, MaxRadius);
            IslandBg.RadiusX = IslandBg.RadiusY = r;
            IslandContent.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        }

        // ---------------------------------------------------------------- Seiten: Musik / Geräte / Erinnerungen

        private void BuildPageDots()
        {
            for (int i = 0; i < 3; i++)
            {
                var page = (Page)i;
                var dot = new Ellipse { Width = 5, Height = 5, Fill = Secondary };
                var hit = new Border { Width = 13, Height = 20, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = dot };
                hit.MouseLeftButtonUp += (_, _) => ShowPage(page);
                PageDots.Children.Add(hit);
            }
            UpdatePageChrome();
        }

        private void ShowPage(Page page)
        {
            if (page == _page) return;
            _page = page;
            FadePanel(MusicPage, page == Page.Music, 0);
            FadePanel(DevicesPage, page == Page.Devices, 0);
            FadePanel(RemindersPage, page == Page.Reminders, 0);
            if (page != Page.Reminders) DisableTyping();
            UpdatePageChrome();
            RefreshPage();
        }

        private void UpdatePageChrome()
        {
            TabMusic.Foreground = _page == Page.Music ? Primary : Secondary;
            TabDevices.Foreground = _page == Page.Devices ? Primary : Secondary;
            RemindersLabel.Foreground = _page == Page.Reminders ? Primary : Secondary;
            for (int i = 0; i < PageDots.Children.Count; i++)
                if (PageDots.Children[i] is Border { Child: Ellipse dot })
                    dot.Fill = i == (int)_page ? Primary : new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A));
        }

        private void RefreshPage()
        {
            if (_state != IslandState.Expanded) return;
            if (_page == Page.Devices) { BuildDevices(); UpdateVolume(); }
            else if (_page == Page.Reminders) BuildReminders();
        }

        private void TabMusic_Click(object sender, MouseButtonEventArgs e) => ShowPage(Page.Music);
        private void TabDevices_Click(object sender, MouseButtonEventArgs e) => ShowPage(Page.Devices);
        private void Reminders_Click(object sender, RoutedEventArgs e) =>
            ShowPage(_page == Page.Reminders ? Page.Music : Page.Reminders);

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // Zuklappen und erst wieder öffnen, wenn die Maus einmal weg war
            _suppressHover = true;
            _hovering = false;
            DisableTyping();
            ApplyState(RestingState());
        }

        private void Focus_Click(object sender, MouseButtonEventArgs e)
        {
            Settings.Current.FocusMode = !Settings.Current.FocusMode;
            Settings.Save();
            UpdateFocusVisuals();
        }

        private void UpdateFocusVisuals()
        {
            bool on = Settings.Current.FocusMode;
            MoonIcon.Fill = on ? FocusPurple : Secondary;
            FocusButton.Background = on ? new SolidColorBrush(Color.FromArgb(0x2E, 0xBF, 0x5A, 0xF2)) : Brushes.Transparent;
            FocusButton.ToolTip = on ? "Fokus an – keine Song-Hinweise" : "Fokus: keine Song-Hinweise";
            CompactMoon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateFooterDate() =>
            FooterDate.Text = DateTime.Now.ToString("ddd., d. MMM", CultureInfo.CurrentCulture);

        private void Art_Click(object sender, MouseButtonEventArgs e)
        {
            // Klick aufs Cover holt die abspielende App nach vorn
            if (_snap == null || string.IsNullOrEmpty(_snap.AppName)) return;
            var app = WindowTracker.Snapshot().FirstOrDefault(r =>
                r.Name.Contains(_snap.AppName, StringComparison.OrdinalIgnoreCase) ||
                r.ExeName.Contains(_snap.AppName, StringComparison.OrdinalIgnoreCase));
            var win = app != null ? WindowTracker.LiveWindows(app).FirstOrDefault() : IntPtr.Zero;
            if (win != IntPtr.Zero) Win32.ForceForeground(win);
        }

        // ---------------------------------------------------------------- Geräte-Seite

        private void BuildDevices()
        {
            DevicesList.Children.Clear();
            string? def = AudioDevices.DefaultId();
            foreach (var d in AudioDevices.Outputs())
            {
                bool isDefault = d.Id == def;
                var icon = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,5 H3 L7,1 V15 L3,11 H0 Z"), Fill = isDefault ? Primary : Secondary,
                    Width = 9, Height = 11, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
                };
                var name = new TextBlock
                {
                    Text = d.Name, FontSize = 12.5, Foreground = isDefault ? Primary : Secondary,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 8, 0),
                };
                var check = new TextBlock
                {
                    Text = isDefault ? "✓" : "", Foreground = Primary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                };
                var row = new Grid { Height = 28 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(icon);
                Grid.SetColumn(name, 1); row.Children.Add(name);
                Grid.SetColumn(check, 2); row.Children.Add(check);

                var b = new Border
                {
                    Child = row, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8),
                    Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = isDefault ? null : "Als Ausgabe verwenden",
                };
                b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
                b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
                string id = d.Id;
                b.MouseLeftButtonUp += (_, _) =>
                {
                    AudioDevices.SetDefault(id);
                    BuildDevices();
                    UpdateVolume();
                };
                DevicesList.Children.Add(b);
            }
            if (DevicesList.Children.Count == 0)
                DevicesList.Children.Add(new TextBlock { Text = "Keine Ausgabegeräte gefunden", Style = (Style)FindResource("SmallGrey"), Margin = new Thickness(2, 6, 0, 0) });
        }

        private void UpdateVolume(float? overrideLevel = null)
        {
            float? vol = overrideLevel ?? AudioDevices.GetVolume();
            bool muted = AudioDevices.GetMute();
            double f = vol ?? 0;
            VolumeFill.Width = VolumeTrack.ActualWidth * f;
            VolumeFill.Opacity = muted ? 0.35 : 1;
            VolumeText.Text = vol == null ? "–" : muted ? "stumm" : $"{Math.Round(f * 100)} %";
            VolumeIcon.Data = muted || f < 0.005 ? SpeakerOff : SpeakerOn;
            VolumeIcon.Fill = Primary;
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            AudioDevices.SetMute(!AudioDevices.GetMute());
            UpdateVolume();
        }

        private void Volume_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _volDragging = true;
            VolumeHit.CaptureMouse();
            SetVolumeAt(e);
        }

        private void Volume_MouseMove(object sender, MouseEventArgs e)
        {
            if (_volDragging) SetVolumeAt(e);
        }

        private void Volume_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_volDragging) return;
            SetVolumeAt(e);
            _volDragging = false;
            VolumeHit.ReleaseMouseCapture();
            if (!_inside) _collapseTimer.Start();
        }

        private void SetVolumeAt(MouseEventArgs e)
        {
            double w = VolumeTrack.ActualWidth;
            if (w <= 0) return;
            float level = (float)Math.Clamp(e.GetPosition(VolumeTrack).X / w, 0, 1);
            if (AudioDevices.GetMute()) AudioDevices.SetMute(false);
            AudioDevices.SetVolume(level);
            UpdateVolume(level);
        }

        /// <summary>Mausrad über der Insel = Lautstärke.</summary>
        private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_page == Page.Reminders && ReminderInput.IsKeyboardFocused) return;
            float? vol = AudioDevices.GetVolume();
            if (vol == null) return;
            float level = Math.Clamp(vol.Value + (e.Delta > 0 ? 0.02f : -0.02f), 0f, 1f);
            AudioDevices.SetVolume(level);
            if (_page == Page.Devices) UpdateVolume(level);
            e.Handled = true;
        }

        // ---------------------------------------------------------------- Erinnerungen

        private static readonly Regex TimePattern = new(@"\b([01]?\d|2[0-3])[:.]([0-5]\d)\b", RegexOptions.Compiled);

        private void BuildReminders()
        {
            RemindersList.Children.Clear();
            var items = Settings.Current.Reminders;
            RemindersEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var r in items.OrderBy(r => r.Due ?? DateTime.MaxValue))
            {
                var item = r;
                var circle = new Ellipse
                {
                    Width = 14, Height = 14, Stroke = Secondary, StrokeThickness = 1.4, Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center, Fill = Brushes.Transparent, ToolTip = "Erledigt",
                };
                circle.MouseEnter += (_, _) => circle.Fill = new SolidColorBrush(Color.FromArgb(0x55, 0xF5, 0xF5, 0xF7));
                circle.MouseLeave += (_, _) => circle.Fill = Brushes.Transparent;
                circle.MouseLeftButtonUp += (_, _) =>
                {
                    Settings.Current.Reminders.Remove(item);
                    Settings.Save();
                    BuildReminders();
                };

                var text = new TextBlock
                {
                    Text = item.Text, FontSize = 12.5, Foreground = Primary, Margin = new Thickness(10, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                };
                bool overdue = item.Due is DateTime due && due <= DateTime.Now;
                var when = new TextBlock
                {
                    Text = item.Due is DateTime d ? (d.Date == DateTime.Today ? d.ToString("HH:mm") : d.ToString("ddd HH:mm", CultureInfo.CurrentCulture)) : "",
                    FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = overdue ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)) : Secondary,
                };

                var row = new Grid { Height = 26, Margin = new Thickness(4, 0, 4, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(circle);
                Grid.SetColumn(text, 1); row.Children.Add(text);
                Grid.SetColumn(when, 2); row.Children.Add(when);
                RemindersList.Children.Add(row);
            }
        }

        private void ReminderInput_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Die Insel nimmt normalerweise keinen Fokus an (damit sie dir nichts wegklaut).
            // Zum Tippen wird das kurz erlaubt.
            long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0)
                NativeMethods.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex & ~Win32.WS_EX_NOACTIVATE));
            Win32.ForceForeground(_hwnd);
            Activate();
            ReminderInput.Focus();
            Keyboard.Focus(ReminderInput);
        }

        private void DisableTyping()
        {
            if (_hwnd == IntPtr.Zero) return;
            if (ReminderInput.IsKeyboardFocused) Keyboard.ClearFocus();
            long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
            if ((ex & Win32.WS_EX_NOACTIVATE) == 0)
                NativeMethods.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_NOACTIVATE));
        }

        private void ReminderInput_TextChanged(object sender, TextChangedEventArgs e) =>
            ReminderPlaceholder.Visibility = ReminderInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void ReminderInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ReminderInput.Text = "";
                DisableTyping();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            string raw = ReminderInput.Text.Trim();
            if (raw.Length == 0) return;

            // „18:30 Wäsche“ oder „Wäsche um 18.30“ → Uhrzeit heute (oder morgen, falls schon vorbei)
            DateTime? due = null;
            string text = raw;
            var m = TimePattern.Match(raw);
            if (m.Success)
            {
                var t = DateTime.Today.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value));
                if (t <= DateTime.Now) t = t.AddDays(1);
                due = t;
                text = Regex.Replace(raw.Remove(m.Index, m.Length), @"\s*\bum\b\s*$|^\s*um\b\s*", " ").Trim();
                if (text.Length == 0) text = "Erinnerung";
            }

            Settings.Current.Reminders.Add(new ReminderItem { Text = text, Due = due });
            Settings.Save();
            ReminderInput.Text = "";
            BuildReminders();
        }

        private void CheckReminders()
        {
            foreach (var r in Settings.Current.Reminders)
            {
                if (r.Notified || r.Due is not DateTime due || due > DateTime.Now) continue;
                r.Notified = true;
                Settings.Save();
                ShowNotice("Erinnerung", r.Text);
                break; // eine nach der anderen
            }
        }

        /// <summary>Kurzer Hinweis in der Insel (auch im Fokus-Modus).</summary>
        private void ShowNotice(string title, string text)
        {
            if (_hovering)
            {
                ShowPage(Page.Reminders);
                return;
            }
            _notice = true;
            PeekTitle.Text = title;
            PeekArtist.Text = text;
            PeekArt.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A));
            PeekArtPh.Text = "⏰";
            PeekArtPh.Foreground = Brushes.Black;
            PeekArtPh.Visibility = Visibility.Visible;
            ApplyState(IslandState.Peek, force: true);
            _peekTimer.Interval = TimeSpan.FromSeconds(6);
            _peekTimer.Stop();
            _peekTimer.Start();
        }

        private void EndNotice()
        {
            if (!_notice) return;
            _notice = false;
            _peekTimer.Interval = TimeSpan.FromSeconds(3.2);
            PeekArtPh.Text = "♪";
            PeekArtPh.Foreground = Secondary;
            ApplyMediaTexts();
            SetArt(PeekArt, PeekArtPh, _art);
        }

        // ---------------------------------------------------------------- Mediendaten

        private bool HasMedia => _snap != null && !string.IsNullOrEmpty(_snap.Title);

        private void ApplyMediaTexts()
        {
            bool has = HasMedia;
            string title = has ? _snap!.Title : "Nichts wird abgespielt";
            string artist = has ? _snap!.Artist : "";
            ExpandedTitle.Text = title;
            ExpandedArtist.Text = artist;
            AppName.Text = has ? _snap!.AppName : "";
            if (!_notice)
            {
                PeekTitle.Text = title;
                PeekArtist.Text = artist;
            }
        }

        private void OnMediaChanged(MediaSnapshot? snap)
        {
            _snap = snap;
            bool hasMedia = HasMedia;
            ApplyMediaTexts();

            bool playing = hasMedia && snap!.IsPlaying;
            PlayIcon.Data = playing ? PauseGeometry : PlayGeometry;
            PlayButton.ToolTip = playing ? "Pause" : "Abspielen";
            PlayButton.IsEnabled = hasMedia && snap!.CanPlayPause;
            NextButton.IsEnabled = hasMedia && snap!.CanNext;
            PrevButton.IsEnabled = hasMedia && snap!.CanPrevious;
            ProgressArea.Visibility = hasMedia && snap!.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
            ProgressHit.IsEnabled = hasMedia && snap!.CanSeek;

            UpdateBars();

            // Cover nur neu laden, wenn sich das Bild geändert haben kann
            string thumbKey = (snap?.TrackKey ?? "") + "|" + (snap?.Thumbnail?.Length ?? 0);
            if (thumbKey != _lastThumbKey)
            {
                _lastThumbKey = thumbKey;
                UpdateArtwork(hasMedia ? snap!.Thumbnail : null);
            }

            UpdateProgress();

            // Neuer Titel → kurz „hereinschauen“ (nicht im Fokus-Modus)
            string trackKey = hasMedia ? snap!.TrackKey : "";
            bool trackChanged = trackKey != _lastTrackKey;
            _lastTrackKey = trackKey;

            if (_hovering || _notice) { _firstSnapshot = false; return; }
            if (hasMedia && trackChanged && !_firstSnapshot && !Settings.Current.FocusMode)
            {
                ApplyState(IslandState.Peek);
                _peekTimer.Stop();
                _peekTimer.Start();
            }
            else if (_state != IslandState.Peek)
            {
                ApplyState(RestingState());
            }
            _firstSnapshot = false;
        }

        private void UpdateArtwork(byte[]? bytes)
        {
            BitmapSource? bmp = null;
            if (bytes != null)
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = new MemoryStream(bytes);
                    bi.DecodePixelWidth = 200;
                    bi.EndInit();
                    bi.Freeze();
                    bmp = bi;
                }
                catch { bmp = null; }
            }
            _art = bmp;

            SetArt(CompactArt, CompactArtPh, bmp);
            if (!_notice) SetArt(PeekArt, PeekArtPh, bmp);
            SetArt(ExpandedArt, ExpandedArtPh, bmp);
        }

        private static void SetArt(Border target, UIElement placeholder, BitmapSource? bmp)
        {
            if (bmp == null)
            {
                target.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
                placeholder.Visibility = Visibility.Visible;
            }
            else
            {
                var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                target.Background = brush;
                placeholder.Visibility = Visibility.Collapsed;
            }
        }

        // ---------------------------------------------------------------- Timer

        /// <summary>Nur die Balken der gerade sichtbaren Ansicht animieren (spart Rechenleistung).</summary>
        private void UpdateBars()
        {
            bool playing = HasMedia && _snap!.IsPlaying && !_hiddenForFullscreen;
            _compactBars.SetPlaying(playing && _state == IslandState.Compact);
            _peekBars.SetPlaying(playing && _state == IslandState.Peek && !_notice);
            _expandedBars.SetPlaying(playing && _state == IslandState.Expanded);
        }

        private void OnTick()
        {
            _tickCount++;
            UpdateProgress();

            if (_tickCount % 4 == 0)
            {
                CheckFullscreen();
                CheckReminders();
                if (!_hiddenForFullscreen) NativeMethods.KeepOnTop(_hwnd);
                if (_state == IslandState.Expanded && _page == Page.Devices && !_volDragging) UpdateVolume();
            }
        }

        private void UpdateProgress()
        {
            if (_state != IslandState.Expanded && !_seeking) return; // unsichtbar → nichts rechnen
            var s = _snap;
            if (s == null || s.Duration <= TimeSpan.Zero) return;

            double fraction = _seeking
                ? _seekFraction
                : s.EstimatedPosition.TotalMilliseconds / s.Duration.TotalMilliseconds;
            fraction = Math.Clamp(fraction, 0, 1);

            double x = ProgressTrack.ActualWidth * fraction;
            ProgressFill.Width = x;
            KnobShift.X = x - 4.5;

            var pos = TimeSpan.FromMilliseconds(s.Duration.TotalMilliseconds * fraction);
            string elapsed = Format(pos), total = Format(s.Duration);
            if (ElapsedText.Text != elapsed) ElapsedText.Text = elapsed;
            if (DurationText.Text != total) DurationText.Text = total;
        }

        private static string Format(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        public void RecheckFullscreen() => CheckFullscreen();

        private void CheckFullscreen()
        {
            // Mit der Einstellung „Insel auch in Spielen zeigen“ bleibt sie einfach sichtbar
            bool fs = !Settings.Current.ShowIslandInFullscreen && NativeMethods.IsForegroundFullscreen(_hwnd);
            if (fs == _hiddenForFullscreen) return;
            _hiddenForFullscreen = fs;
            UpdateBars();

            var anim = new DoubleAnimation(fs ? 0 : 1, TimeSpan.FromMilliseconds(200));
            if (fs) anim.Completed += (_, _) => { if (_hiddenForFullscreen) Visibility = Visibility.Hidden; };
            else Visibility = Visibility.Visible;
            BeginAnimation(OpacityProperty, anim);
        }

        // ---------------------------------------------------------------- Steuerung

        private async void Play_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
        private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
        private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

        private void Progress_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_snap == null || !_snap.CanSeek) return;
            _seeking = true;
            ProgressHit.CaptureMouse();
            _seekFraction = FractionAt(e);
            UpdateProgress();
        }

        private void Progress_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_seeking) return;
            _seekFraction = FractionAt(e);
            UpdateProgress();
        }

        private async void Progress_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_seeking) return;
            _seekFraction = FractionAt(e);
            ProgressHit.ReleaseMouseCapture();
            var target = _snap != null
                ? TimeSpan.FromMilliseconds(_snap.Duration.TotalMilliseconds * _seekFraction)
                : TimeSpan.Zero;
            // Lokal sofort übernehmen, damit der Balken nicht zurückspringt, bis die App die neue Position meldet
            if (_snap != null) _snap = _snap with { Position = target, PositionUpdatedAt = DateTimeOffset.Now };
            _seeking = false;
            await _media.SeekAsync(target);

            if (!_inside) _collapseTimer.Start();
        }

        private double FractionAt(MouseEventArgs e)
        {
            double w = ProgressTrack.ActualWidth;
            return w <= 0 ? 0 : Math.Clamp(e.GetPosition(ProgressTrack).X / w, 0, 1);
        }
    }
}
