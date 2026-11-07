using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace Isle
{
    public partial class TopBar : Window
    {
        public const double BarHeight = 30;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private AppBar? _appBar;
        private int _ticks;
        private DateTime _lastWeather = DateTime.MinValue;

        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);

        /// <summary>Stufen für den Schriftstärke-Regler (Segoe UI hat genau diese Schnitte).</summary>
        public static readonly (string Name, FontWeight Weight)[] Weights =
        {
            ("Dünn", FontWeights.Light),
            ("Leicht", FontWeight.FromOpenTypeWeight(350)),
            ("Normal", FontWeights.Normal),
            ("Halbfett", FontWeights.SemiBold),
            ("Fett", FontWeights.Bold),
        };

        /// <summary>Schriftstärke aus den Einstellungen übernehmen. Der App-Name ist eine Stufe kräftiger.</summary>
        public void ApplyFontWeight()
        {
            int i = Math.Clamp(Settings.Current.BarFontWeight, 0, Weights.Length - 1);
            FontWeight = Weights[i].Weight;
            ActiveApp.FontWeight = Weights[Math.Min(Weights.Length - 1, i + 1)].Weight;
        }

        public TopBar()
        {
            InitializeComponent();
            ApplyFontWeight();
            _timer.Tick += (_, _) => OnTick();
            TrayHost.Changed += () => { if (TrayPopup.IsOpen) BuildTrayIcons(); };
            // Hintergrundbild geändert / Bildschirm umgestellt → Schriftfarbe neu bestimmen
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, ev) =>
            {
                if (ev.Category is Microsoft.Win32.UserPreferenceCategory.Desktop or Microsoft.Win32.UserPreferenceCategory.General)
                    ToneSoon(1.5); // Windows blendet neue Bilder weich ein – kurz warten
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => ToneSoon(1.5);

            Loaded += (_, _) =>
            {
                UpdateTone(force: true);
                ToneSoon(2.5); // nochmal, sobald Desktop und Fenster fertig gezeichnet sind
                OnTick();
                UpdateActiveApp();
                UpdateBattery();
                _ = UpdateWeatherAsync();
                _timer.Start();
            };
        }

        private bool _fullscreen;
        private bool _revealed;

        /// <summary>Windows-Taste/Start im Spiel: Leiste über das Vollbild legen, bis das Startmenü wieder zu ist.</summary>
        public void RevealOverFullscreen()
        {
            _revealed = true;
            Visibility = Visibility.Visible;
            if (_barHwnd != IntPtr.Zero) NativeMethods.KeepOnTop(_barHwnd);
        }

        public void EndReveal()
        {
            if (!_revealed) return;
            _revealed = false;
            Visibility = _fullscreen && NativeMethods.IsForegroundFullscreen(_barHwnd) ? Visibility.Hidden : Visibility.Visible;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            _barHwnd = hwnd;
            NativeMethods.MakeToolWindow(hwnd);
            _appBar = new AppBar(this, Win32.ABE_TOP, BarHeight, BarHeight);
            _appBar.FullscreenChanged += fs =>
            {
                _fullscreen = fs;
                if (!_revealed) Visibility = fs ? Visibility.Hidden : Visibility.Visible;
            };
            _appBar.Register();
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        public void ReregisterAppBar()
        {
            _appBar?.Unregister();
            _appBar?.Register();
        }

        private static readonly uint TaskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Windows schickt das an alle Fenster, wenn der Explorer (neu) gestartet ist
            if (msg == (int)TaskbarCreatedMsg) ((App)Application.Current).OnExplorerRestarted();
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _appBar?.Dispose();
            base.OnClosed(e);
        }

        // ---------------------------------------------------------------- Laufende Aktualisierung

        private void OnTick()
        {
            _ticks++;
            // Texte nur anfassen, wenn sie sich ändern – sonst rechnet WPF jedes Mal das Layout neu
            SetText(ClockText, DateTime.Now.ToString("ddd d. MMM   HH:mm", CultureInfo.CurrentCulture));
            UpdateLanguage();

            if (_ticks % 5 == 0) UpdateActiveApp(); // Sicherheitsnetz; normal kommt das per Ereignis
            if (_ticks % 3 == 0)
            {
                SetVisible(CamDot, PrivacyMonitor.InUse("webcam"));
                SetVisible(MicDot, PrivacyMonitor.InUse("microphone"));
            }
            if (_ticks % 20 == 0) UpdateBattery();
            if (_ticks % 10 == 0) UpdateTone(force: false); // Diashow, verschobene Fenster …
            if ((DateTime.Now - _lastWeather).TotalMinutes >= 15) _ = UpdateWeatherAsync();
        }

        // ---------------------------------------------------------------- Hell/Dunkel je nach Hintergrundbild

        private WallpaperTone.Result? _tone;
        private bool? _leftLight, _rightLight;
        private bool _measuring;

        /// <summary>Misst das Hintergrundbild hinter der Leiste (im Hintergrund, damit nichts ruckelt).</summary>
        private IntPtr _barHwnd;

        /// <summary>
        /// Bestimmt die Schriftfarbe. Zuerst wird direkt gemessen, was hinter der Leiste auf dem Bildschirm ist;
        /// geht das nicht, wird die Hintergrundbild-Datei ausgewertet. Alles landet im Protokoll (isle.log).
        /// </summary>
        private async void UpdateTone(bool force)
        {
            if (_measuring) return;

            string mode = Settings.Current.BarTextMode;
            if (mode is "dark" or "light")
            {
                bool light = mode == "dark"; // „immer dunkel“ = dunkle Schrift = wie auf hellem Grund
                _leftLight = _rightLight = light;
                SetTone(LeftGroup, LeftHalo, light);
                SetTone(RightGroup, RightHalo, light);
                return;
            }

            _measuring = true;
            try
            {
                int barH = (int)Math.Round(BarHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
                var screen = await ScreenTone.SampleAsync(_barHwnd, barH);
                if (screen is (double l, double r))
                {
                    ApplyLuma(l, r, "Bildschirm");
                    return;
                }

                // Ersatz: Hintergrundbild-Datei auswerten (im Hintergrund, damit nichts ruckelt)
                if (!force && _tone != null)
                {
                    string? path = WallpaperTone.CurrentPath();
                    DateTime stamp = path != null && System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                    if (path == _tone.Source && stamp == _tone.SourceTime) return;
                }
                var mon = Win32.PrimaryMonitorRect();
                int sw = mon.Right - mon.Left, sh = mon.Bottom - mon.Top;
                var tcs = new System.Threading.Tasks.TaskCompletionSource<WallpaperTone.Result?>();
                StaWorker.Run(() =>
                {
                    try { tcs.TrySetResult(WallpaperTone.Measure(sw, sh, barH)); }
                    catch (Exception ex) { Log.Write("Bilddatei-Messung fehlgeschlagen: " + ex.Message); tcs.TrySetResult(null); }
                });
                var file = await tcs.Task;
                if (file != null)
                {
                    _tone = file;
                    ApplyLuma(file.Left, file.Right, "Bilddatei " + (file.Source ?? "(einfarbig)"));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Helligkeit konnte nicht bestimmt werden: " + ex.Message);
            }
            finally
            {
                _measuring = false;
            }
        }

        private void ApplyLuma(double left, double right, string source)
        {
            bool? oldL = _leftLight, oldR = _rightLight;
            _leftLight = IsLight(left, _leftLight);
            _rightLight = IsLight(right, _rightLight);
            SetTone(LeftGroup, LeftHalo, _leftLight.Value);
            SetTone(RightGroup, RightHalo, _rightLight.Value);
            if (oldL != _leftLight || oldR != _rightLight)
                Log.Write($"Top-Bar: Helligkeit links {left:0.00}, rechts {right:0.00} ({source}) → Schrift links " +
                          $"{(_leftLight.Value ? "dunkel" : "hell")}, rechts {(_rightLight.Value ? "dunkel" : "hell")}");
        }

        private void ToneSoon(double seconds)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                t.Tick += (_, _) => { t.Stop(); UpdateTone(force: true); };
                t.Start();
            }));
        }

        public void SetTextMode(string mode)
        {
            Settings.Current.BarTextMode = mode;
            Settings.Save();
            _leftLight = _rightLight = null;
            UpdateTone(force: true);
        }

        /// <summary>Mit etwas Spielraum, damit es bei mittelhellen Bildern nicht hin und her springt.</summary>
        private static bool IsLight(double luma, bool? before) => before switch
        {
            true => luma > 0.48,
            false => luma > 0.62,
            null => luma > 0.55,
        };

        private static readonly Brush LightFg = Frozen(0x1C, 0x1C, 0x1E), LightDim = Frozen(0x48, 0x48, 0x4A);
        private static readonly Brush DarkFg = Frozen(0xF5, 0xF5, 0xF7), DarkDim = Frozen(0xD1, 0xD1, 0xD6);
        private static readonly Brush LightHover = Frozen(0x1A, 0, 0, 0, alpha: true), LightPressed = Frozen(0x2E, 0, 0, 0, alpha: true);
        private static readonly Brush DarkHover = Frozen(0x26, 0xFF, 0xFF, 0xFF, alpha: true), DarkPressed = Frozen(0x3D, 0xFF, 0xFF, 0xFF, alpha: true);

        private static Brush Frozen(byte a, byte b, byte c, byte d = 0, bool alpha = false)
        {
            var brush = new SolidColorBrush(alpha ? Color.FromArgb(a, b, c, d) : Color.FromRgb(a, b, c));
            brush.Freeze();
            return brush;
        }

        /// <summary>Heller Hintergrund → dunkle Schrift mit hellem Schein, dunkler Hintergrund → weiße Schrift mit dunklem Schein.</summary>
        private static void SetTone(FrameworkElement group, System.Windows.Media.Effects.DropShadowEffect halo, bool light)
        {
            group.Resources["BarFg"] = light ? LightFg : DarkFg;
            group.Resources["BarFgDim"] = light ? LightDim : DarkDim;
            group.Resources["BarHover"] = light ? LightHover : DarkHover;
            group.Resources["BarPressed"] = light ? LightPressed : DarkPressed;
            halo.Color = light ? Colors.White : Colors.Black;
            halo.Opacity = light ? 0.6 : 0.5;
        }

        /// <summary>Windows meldet: andere App ist jetzt vorne.</summary>
        public void OnForegroundChanged()
        {
            UpdateActiveApp();
            UpdateLanguage();
        }

        private void UpdateActiveApp()
        {
            string app = WindowTracker.ForegroundAppName();
            if (app.Length > 0) SetText(ActiveApp, app);
        }

        private static void SetText(TextBlock tb, string text)
        {
            if (tb.Text != text) tb.Text = text;
        }

        private static void SetVisible(UIElement e, bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            if (e.Visibility != v) e.Visibility = v;
        }

        private int _lastLangId;

        private void UpdateLanguage()
        {
            try
            {
                IntPtr fg = Win32.GetForegroundWindow();
                uint tid = Win32.GetWindowThreadProcessId(fg, out _);
                int langId = GetKeyboardLayout(tid).ToInt32() & 0xFFFF;
                if (langId == 0 || langId == _lastLangId) return;
                _lastLangId = langId;
                LangText.Text = new CultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant();
            }
            catch { }
        }

        private void UpdateBattery()
        {
            var ps = WinForms.SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery) ||
                ps.BatteryChargeStatus == WinForms.BatteryChargeStatus.Unknown)
            {
                BatteryPanel.Visibility = Visibility.Collapsed;
                return;
            }
            BatteryPanel.Visibility = Visibility.Visible;
            double pct = Math.Clamp(ps.BatteryLifePercent, 0, 1);
            BatteryText.Text = $"{Math.Round(pct * 100)} %";
            BatteryFill.Width = Math.Max(2, 18 * pct);
            bool charging = ps.PowerLineStatus == WinForms.PowerLineStatus.Online;
            ChargingBolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
            if (charging) BatteryFill.Background = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
            else if (pct <= 0.2) BatteryFill.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            else BatteryFill.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BarFg"); // folgt hell/dunkel
        }

        private async System.Threading.Tasks.Task UpdateWeatherAsync()
        {
            _lastWeather = DateTime.Now;
            var s = Settings.Current;
            if (s.WeatherLat is not double lat || s.WeatherLon is not double lon)
            {
                WeatherIcon.Text = "☁";
                WeatherText.Text = "Ort?";
                WeatherButton.ToolTip = "Klicken, um deinen Ort fürs Wetter festzulegen";
                return;
            }
            var w = await Weather.CurrentAsync(lat, lon);
            if (w == null) return;
            WeatherIcon.Text = w.Symbol;
            WeatherText.Text = $"{Math.Round(w.Temperature)}°";
            WeatherButton.ToolTip = $"{s.WeatherCity}: {w.Description}";
        }

        public async void AskWeatherCity()
        {
            string? city = DarkDialog.Prompt("Wetter-Ort", "Für welche Stadt soll das Wetter angezeigt werden?",
                Settings.Current.WeatherCity ?? "");
            if (string.IsNullOrWhiteSpace(city)) return;

            WeatherText.Text = "…";
            var geo = await Weather.GeocodeAsync(city);
            if (geo == null)
            {
                DarkDialog.Confirm("Ort nicht gefunden", $"Zu „{city}“ wurde nichts gefunden. Versuch es mit einer größeren Stadt in der Nähe.", "OK");
                await UpdateWeatherAsync();
                return;
            }
            Settings.Current.WeatherCity = geo.Value.name;
            Settings.Current.WeatherLat = geo.Value.lat;
            Settings.Current.WeatherLon = geo.Value.lon;
            Settings.Save();
            await UpdateWeatherAsync();
        }

        // ---------------------------------------------------------------- Klicks

        private void Weather_Click(object sender, RoutedEventArgs e) => AskWeatherCity();

        // ---------------------------------------------------------------- Tray-Icons

        private DispatcherTimer? _trayCloseTimer;

        private void Tray_Click(object sender, RoutedEventArgs e)
        {
            // Ohne eigenen Tray-Empfänger: wie früher die Windows-Taskleiste kurz zeigen
            if (!Settings.Current.TrayInTopBar || !TrayHost.Active)
            {
                TaskbarControl.Peek();
                return;
            }
            if (Menus.IsOpenFor(TrayButton))
            {
                Menus.Close();
                return;
            }
            BuildTrayIcons();
            Menus.Open(TrayPopup, TrayButton);
        }

        private void TrayShowTaskbar_Click(object sender, RoutedEventArgs e)
        {
            Menus.Close();
            TaskbarControl.Peek();
        }

        private void BuildTrayIcons()
        {
            TrayIconsPanel.Children.Clear();
            var icons = TrayHost.Icons
                .Where(i => { Win32.GetWindowThreadProcessId(i.Hwnd, out uint pid); return pid != (uint)Environment.ProcessId; })
                .ToList();
            TrayEmpty.Visibility = icons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var icon in icons)
            {
                var img = new Image { Source = icon.Icon, Width = 18, Height = 18 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                var cell = new Border
                {
                    Width = 36, Height = 32, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent,
                    Child = img, Cursor = Cursors.Hand,
                    ToolTip = string.IsNullOrWhiteSpace(icon.Tip) ? null : icon.Tip.Trim(),
                };
                var hover = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E));
                cell.MouseEnter += (_, _) => cell.Background = hover;
                cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;

                cell.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    var at = Anchor(cell);
                    if (e.ClickCount >= 2)
                    {
                        TrayHost.DoubleClick(icon, at);
                        Menus.Close();
                        return;
                    }
                    TrayHost.LeftClick(icon, at);
                    // Kurz offen lassen, falls ein Doppelklick folgt – dann zu
                    _trayCloseTimer?.Stop();
                    _trayCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    _trayCloseTimer.Tick += (_, _) => { _trayCloseTimer?.Stop(); Menus.Close(); };
                    _trayCloseTimer.Start();
                };
                cell.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    TrayHost.RightClick(icon, Anchor(cell)); // das Programm öffnet sein eigenes Menü an der Maus
                    Menus.Close();
                };
                TrayIconsPanel.Children.Add(cell);
            }
        }

        private static Point Anchor(FrameworkElement e) => e.PointToScreen(new Point(e.ActualWidth / 2, e.ActualHeight / 2));

        private void Clock_Click(object sender, RoutedEventArgs e) =>
            Win32.SendCombo(0x5B /* Win */, 0x4E /* N */);

        private void Logo_Click(object sender, RoutedEventArgs e)
        {
            // Nochmal aufs Menüzeichen = Menü zu
            if (Menus.IsOpenFor(LogoButton))
            {
                Menus.Close();
                return;
            }
            Menus.Open(BuildIsleMenu(), LogoButton, PlacementMode.Bottom, 3, toggle: LogoButton);
        }

        private ContextMenu BuildIsleMenu()
        {
            var app = (App)Application.Current;
            var s = Settings.Current;
            var m = new ContextMenu();

            m.Items.Add(Item("Isle-Start öffnen", app.ToggleStartMenu, gesture: "Win"));
            m.Items.Add(Item("Einstellungen …", SettingsWindow.ShowOrFocus));
            m.Items.Add(new Separator());
            m.Items.Add(Check("Dock anzeigen", s.ShowDock, v => app.SetDockVisible(v)));
            m.Items.Add(Check("Windows-Taskleiste ausblenden", s.AutoHideTaskbar, v => app.SetAutoHideTaskbar(v)));
            m.Items.Add(Check("Tray-Icons in der Top-Bar (Pfeil oben rechts)", s.TrayInTopBar, v => app.SetTrayInTopBar(v)));
            m.Items.Add(Item("Windows-Taskleiste kurz zeigen", TaskbarControl.Peek));
            m.Items.Add(Item("Apps aus Windows-Taskleiste ins Dock holen", () =>
            {
                int n = app.Dock?.ImportTaskbarPins() ?? 0;
                DarkDialog.Confirm("Taskleiste übernommen",
                    n == 0 ? "Alle Apps aus deiner Windows-Taskleiste sind schon im Dock." :
                    n == 1 ? "1 App wurde hinten ans Dock angehängt." : $"{n} Apps wurden hinten ans Dock angehängt.", "OK");
            }));
            m.Items.Add(Check("Windows-Taste öffnet Isle-Start", s.WinKeyOpensStart, v => app.SetWinKeyOpensStart(v)));
            m.Items.Add(Check("Insel auch in Spielen/Vollbild zeigen", s.ShowIslandInFullscreen, v => app.SetShowIslandInFullscreen(v)));
            m.Items.Add(Check("Mit Windows starten", Autostart.IsEnabled(), Autostart.Set));
            m.Items.Add(Item("Wetter-Ort festlegen …", AskWeatherCity));
            m.Items.Add(new Separator());
            string tm = s.BarTextMode;
            m.Items.Add(Check("Leiste: Schrift automatisch", tm == "auto", _ => SetTextMode("auto")));
            m.Items.Add(Check("Leiste: Schrift immer dunkel", tm == "dark", _ => SetTextMode("dark")));
            m.Items.Add(Check("Leiste: Schrift immer hell", tm == "light", _ => SetTextMode("light")));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Windows-Einstellungen", () => AppCatalog.Open("ms-settings:")));
            m.Items.Add(Item("Task-Manager", () => AppCatalog.Open("taskmgr.exe"), gesture: "Strg+Umschalt+Esc"));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Sperren", Power.Lock, gesture: "Win+L"));
            m.Items.Add(Item("Energie sparen", Power.Sleep));
            m.Items.Add(Item("Neu starten …", Power.Restart));
            m.Items.Add(Item("Herunterfahren …", Power.Shutdown));
            m.Items.Add(Item("Abmelden …", Power.SignOut));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Isle beenden", app.Quit, gesture: "Strg+Alt+Q"));
            return m;
        }

        internal static MenuItem Item(string header, Action action, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) =>
            {
                if (mi.Parent is ContextMenu cm) cm.IsOpen = false; // erst Menü zu, dann ausführen
                action();
            };
            return mi;
        }

        internal static MenuItem Check(string header, bool isChecked, Action<bool> onChange)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            mi.Click += (_, _) =>
            {
                if (mi.Parent is ContextMenu cm) cm.IsOpen = false;
                onChange(mi.IsChecked);
            };
            return mi;
        }
    }
}
