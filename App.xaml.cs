using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Isle
{
    public partial class App : Application
    {
        private Mutex? _singleInstance;
        private TrayIcon? _tray;
        private MainWindow? _island;
        private TopBar? _topBar;
        private StartMenu? _startMenu;
        private KeyboardHook? _keyboard;
        private WinEvents? _events;
        private bool _quitting;
        private EventWaitHandle? _quitSignal;
        private const string QuitSignalName = "Isle.Quit.Signal";

        public Dock? Dock { get; private set; }
        public TopBar? TopBarWindow => _topBar;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- Sondermodi ohne Oberfläche ----
            // Isle.exe --watchdog <pid>: wartet, bis Isle endet, und holt dann die Taskleiste zurück
            if (e.Args.Length == 2 && e.Args[0] == "--watchdog" && int.TryParse(e.Args[1], out int parentPid))
            {
                RunWatchdog(parentPid);
                return;
            }
            // Isle.exe --quit: bittet eine laufende Isle, sich sauber zu beenden (für build.bat)
            if (e.Args.Length == 1 && e.Args[0] == "--quit")
            {
                if (EventWaitHandle.TryOpenExisting(QuitSignalName, out var signal)) signal.Set();
                Shutdown();
                return;
            }

            // Nur eine Instanz gleichzeitig
            var mutex = new Mutex(true, "Isle.DynamicIsland.SingleInstance", out bool isNew);
            if (!isNew)
            {
                // Läuft schon – diese Kopie beendet sich, ohne die Taskleiste der laufenden anzufassen
                mutex.Dispose();
                Shutdown();
                return;
            }
            _singleInstance = mutex;

            // Falls etwas schiefgeht: Taskleiste immer zurückstellen
            AppDomain.CurrentDomain.UnhandledException += (_, _) => TaskbarControl.Restore();
            DispatcherUnhandledException += (_, args) =>
            {
                // Einzelne Fehler sollen nicht die ganze Oberfläche abstürzen lassen
                args.Handled = true;
            };
            SessionEnding += (_, _) => Cleanup();

            // Sauberes Beenden auf Zuruf von außen (Isle.exe --quit)
            _quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName);
            ThreadPool.RegisterWaitForSingleObject(_quitSignal,
                (_, _) => Dispatcher.BeginInvoke(new Action(Quit)), null, Timeout.Infinite, true);

            Settings.Load();
            var s = Settings.Current;

            if (s.AutoHideTaskbar)
            {
                TaskbarControl.Apply(true);
                StartWatchdog();
            }

            // Tray-Icons aller Programme selbst empfangen (für den Pfeil in der Top-Bar)
            if (s.TrayInTopBar) TrayHost.Start();

            // Windows meldet Fensteränderungen → Dock und Top-Bar reagieren sofort statt ständig abzufragen
            _events = new WinEvents();

            _topBar = new TopBar();
            _topBar.Show();

            Dock = new Dock();
            if (s.ShowDock) Dock.Show();
            _events.WindowsChanged += () => Dock.Refresh();
            _events.ForegroundChanged += _topBar.OnForegroundChanged;
            _events.ForegroundChanged += Menus.Close; // andere App angeklickt/gewechselt → offenes Menü zu

            _island = new MainWindow();
            _island.Show();

            _startMenu = new StartMenu();

            _keyboard = new KeyboardHook { Enabled = s.WinKeyOpensStart };
            _keyboard.WinKeyAlone += ToggleStartMenu;

            _tray = new TrayIcon();

            AppCatalog.Refresh();

            // Rand-Wächter: maximierte Fenster sollen Top-Bar und Dock nie überdecken
            _workAreaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _workAreaTimer.Tick += (_, _) => WorkAreaGuard.Ensure();
            _workAreaTimer.Start();
            SystemParameters.StaticPropertyChanged += (_, ev) =>
            {
                if (ev.PropertyName == nameof(SystemParameters.WorkArea) && !_quitting)
                    Dispatcher.BeginInvoke(new Action(WorkAreaGuard.Ensure), DispatcherPriority.Background);
            };
            Dispatcher.BeginInvoke(new Action(WorkAreaGuard.Ensure), DispatcherPriority.ApplicationIdle);
        }

        private DispatcherTimer? _workAreaTimer;

        // ---------------------------------------------------------------- Aktionen für Menüs

        public void ToggleStartMenu() => _startMenu?.Toggle();

        public void SetDockVisible(bool visible)
        {
            Settings.Current.ShowDock = visible;
            Settings.Save();
            if (Dock == null) return;
            if (visible) Dock.ShowDock(); else Dock.HideDock();
        }

        public void SetAutoHideTaskbar(bool autoHide)
        {
            Settings.Current.AutoHideTaskbar = autoHide;
            Settings.Save();
            TaskbarControl.Apply(autoHide);
            if (autoHide) StartWatchdog();
        }

        // ---------------------------------------------------------------- Absicherung

        private bool _watchdogStarted;

        /// <summary>
        /// Startet einen kleinen zweiten Isle-Prozess ohne Fenster. Stürzt Isle ab oder wird hart beendet
        /// (Task-Manager), holt er sofort die Windows-Taskleiste zurück – du stehst nie ohne da.
        /// </summary>
        private void StartWatchdog()
        {
            if (_watchdogStarted || Environment.ProcessPath == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath, $"--watchdog {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                _watchdogStarted = true;
            }
            catch { }
        }

        private void RunWatchdog(int parentPid)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }
            catch { /* schon weg */ }
            TaskbarControl.EmergencyRestore();
            TrayHost.AskAppsToReregister(); // Tray-Icons sicher wieder bei Windows anmelden lassen
            Shutdown();
        }

        /// <summary>
        /// Explorer wurde neu gestartet (Absturz, Task-Manager …): Er zeigt die Taskleiste wieder und hat
        /// Top-Bar und Dock vergessen. Kurz warten, dann alles neu anmelden und Taskleiste wieder verstecken.
        /// </summary>
        public void OnExplorerRestarted()
        {
            // Unsere eigene Rundmail (Tray-Icons neu anmelden) ist kein Explorer-Neustart
            if (DateTime.Now < TrayHost.IgnoreTaskbarCreatedUntil) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (Settings.Current.AutoHideTaskbar) TaskbarControl.Apply(true);
                _topBar?.ReregisterAppBar();
                if (Settings.Current.ShowDock) Dock?.ReregisterAppBar();
            };
            t.Start();
        }

        public void SetTrayInTopBar(bool enabled)
        {
            Settings.Current.TrayInTopBar = enabled;
            Settings.Save();
            if (enabled) TrayHost.Start(); else TrayHost.Stop();
        }

        public void SetWinKeyOpensStart(bool enabled)
        {
            Settings.Current.WinKeyOpensStart = enabled;
            Settings.Save();
            if (_keyboard != null) _keyboard.Enabled = enabled;
        }

        public void Quit()
        {
            if (_quitting) return;
            _quitting = true;
            _workAreaTimer?.Stop();
            Cleanup();
            _island?.Close();
            _topBar?.Close();
            Dock?.Close();
            _startMenu?.Close();
            Shutdown();
        }

        private void Cleanup()
        {
            _keyboard?.Dispose();
            _keyboard = null;
            _events?.Dispose();
            _events = null;
            _tray?.Dispose();
            _tray = null;
            TrayHost.Stop(); // Programme melden ihre Tray-Icons danach wieder nur bei Windows an
            TaskbarControl.Restore();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstance != null) Cleanup(); // nur in der echten Isle, nicht im Wächter
            _singleInstance?.Dispose();
            _quitSignal?.Dispose();
            base.OnExit(e);
        }
    }
}
