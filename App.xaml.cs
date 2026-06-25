using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using VISOR.Diagnostics;
using VISOR.Telemetry;
using VISOR.Views;
using VISOR.Settings;

namespace VISOR
{
    public partial class App : Application
    {
        // Machine-wide single-instance guard. The mutex enforces "one VISOR at a
        // time"; the event lets a second launch ask the running instance to come
        // to the foreground before it exits.
        private const string SingleInstanceMutexName = @"Global\VISOR_SingleInstance_Mutex";
        private const string ShowInstanceEventName = @"Global\VISOR_ShowInstance_Event";

        private Mutex? _singleInstanceMutex;
        private EventWaitHandle? _showInstanceEvent;
        private Thread? _showInstanceListener;
        private volatile bool _shuttingDown;
        private bool _isPrimaryInstance;

        private SVappsLABSDKWrapper _sdkWrapper = null!;
        private MainWindow _mainWindow = null!;
        private RadarWindow? _radarWindow;
        private ConfigWindow _configWindow = null!;

        public RadarWindow? CurrentRadarWindow => _radarWindow;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!EnsureSingleInstance())
            {
                // Another VISOR is already running - we signalled it to come to
                // the foreground, so this duplicate launch just exits quietly.
                Shutdown();
                return;
            }

            try
            {
                var settings = UserSettings.Instance;
                Log.DebugModeEnabled = settings.DebugModeEnabled;
                Log.StartNewSession();

                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";
                Log.Info($"VISOR started - Version: {version}");

                var visibleRows = string.Join(",", new[] {
                    settings.ShowRow0 ? "0" : null,
                    settings.ShowRow1 ? "1" : null,
                    settings.ShowRow2 ? "2" : null,
                    settings.ShowRow3 ? "3" : null,
                    settings.ShowRow4 ? "4" : null,
                    settings.ShowRow5 ? "5" : null
                }.Where(r => r != null));
                Log.Info($"Settings: size={settings.WindowSize}, rows=[{visibleRows}], radar={settings.ShowRadar}, debug={settings.DebugModeEnabled}");

                Log.CleanupOldLogs();

                Log.Info("Application startup initiated");

                _sdkWrapper = new SVappsLABSDKWrapper();

                bool initialized = await _sdkWrapper.Initialize();
                Log.Info($"SDK initialization result: {initialized}");

                if (!initialized)
                {
                    Log.Error("Failed to initialize telemetry connection");
                    MessageBox.Show("Failed to initialize telemetry connection.", "Initialization Error");
                    Shutdown();
                    return;
                }

                LaunchAllWindows();
            }
            catch (Exception ex)
            {
                Log.Error("EXCEPTION in OnStartup", ex);
                MessageBox.Show($"Application startup error: {ex.Message}\n\nFull details:\n{ex}", "Startup Error");
                Shutdown();
            }
        }

        /// <summary>
        /// Acquires the machine-wide single-instance lock. Returns true if this is
        /// the primary (and only) instance; false if another instance already owns
        /// the lock, in which case that instance is signalled to come to the front.
        /// </summary>
        private bool EnsureSingleInstance()
        {
            try
            {
                _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
                _showInstanceEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowInstanceEventName);

                if (!createdNew)
                {
                    // Tell the running instance to surface itself, then bow out.
                    _showInstanceEvent.Set();
                    return false;
                }

                // We are the primary instance - listen for future launches asking
                // us to come to the foreground.
                _isPrimaryInstance = true;
                StartShowInstanceListener();
                return true;
            }
            catch (Exception ex)
            {
                // If the guard can't be established (e.g. constrained permissions on
                // the Global namespace), fail open and let the app start normally
                // rather than blocking the user entirely.
                Log.Error("Single-instance guard could not be initialized; continuing without it", ex);
                return true;
            }
        }

        private void StartShowInstanceListener()
        {
            _showInstanceListener = new Thread(() =>
            {
                while (!_shuttingDown)
                {
                    try
                    {
                        if (_showInstanceEvent!.WaitOne())
                        {
                            if (_shuttingDown)
                                return;

                            Dispatcher.BeginInvoke(new Action(BringToForeground));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Error in single-instance listener", ex);
                        return;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "VISOR.ShowInstanceListener"
            };
            _showInstanceListener.Start();
        }

        private void BringToForeground()
        {
            try
            {
                Log.Info("Duplicate launch detected - bringing existing instance to foreground");

                var target = _configWindow ?? (Window?)_mainWindow;
                if (target == null)
                    return;

                if (target.WindowState == WindowState.Minimized)
                    target.WindowState = WindowState.Normal;

                target.Show();
                target.Activate();

                // Nudge it above other windows without leaving it pinned topmost.
                bool wasTopmost = target.Topmost;
                target.Topmost = true;
                target.Topmost = wasTopmost;
            }
            catch (Exception ex)
            {
                Log.Error("Error bringing existing instance to foreground", ex);
            }
        }

        private void LaunchAllWindows()
        {
            try
            {
                _mainWindow = new MainWindow(_sdkWrapper);

                var settingsManager = SettingsManager.Instance;
                if (settingsManager.IsRadarVisible)
                {
                    _radarWindow = new RadarWindow(_sdkWrapper, _mainWindow.ViewModel.ClassColorManager);
                }

                _configWindow = new ConfigWindow(_sdkWrapper, _mainWindow);

                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.ShowActivated = true;
                _mainWindow.Show();
                _mainWindow.Activate();

                if (_radarWindow != null)
                {
                    _radarWindow.WindowState = WindowState.Normal;
                    _radarWindow.ShowActivated = true;
                    _radarWindow.Show();
                }

                _configWindow.WindowState = WindowState.Normal;
                _configWindow.ShowActivated = true;
                _configWindow.Show();

                MainWindow = _mainWindow;

                _mainWindow.Closed += OnMainWindowClosed;
                if (_radarWindow != null)
                    _radarWindow.Closed += OnRadarWindowClosed;
                _configWindow.Closed += OnConfigWindowClosed;
                _configWindow.ExitRequested += OnConfigExitRequested;

                Log.Info("All windows launched successfully");
            }
            catch (Exception ex)
            {
                Log.Error("EXCEPTION in LaunchAllWindows", ex);
                MessageBox.Show($"Error launching application windows: {ex.Message}\n\nFull details:\n{ex}", "Launch Error");
                Shutdown();
            }
        }

        private void OnMainWindowClosed(object? sender, EventArgs e)
        {
            Log.Info("MainWindow closed - shutting down application");
            Shutdown();
        }

        private void OnRadarWindowClosed(object? sender, EventArgs e)
        {
            Log.Info("RadarWindow closed independently");
        }

        private void OnConfigWindowClosed(object? sender, EventArgs e)
        {
            Log.Info("ConfigWindow closed independently");
        }

        private void OnConfigExitRequested(object? sender, EventArgs e)
        {
            Log.Info("Config window requested application exit");
            Shutdown();
        }

        public void ShowRadarWindow()
        {
            if (_radarWindow == null && _mainWindow != null)
            {
                Log.Info("Creating RadarWindow on demand");
                _radarWindow = new RadarWindow(_sdkWrapper, _mainWindow.ViewModel.ClassColorManager);
                _radarWindow.Closed += OnRadarWindowClosed;
                _radarWindow.Show();
            }
            else if (_radarWindow != null)
            {
                _radarWindow.Show();
                _radarWindow.Activate();
            }
        }

        public void HideRadarWindow()
        {
            _radarWindow?.Hide();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                Log.Info("Application exit initiated");
                _sdkWrapper?.Shutdown();

                // Tear down the single-instance guard (primary instance only): wake
                // the listener so it can exit, then release the lock so a future
                // launch can start cleanly.
                if (_isPrimaryInstance)
                {
                    _shuttingDown = true;
                    _showInstanceEvent?.Set();
                    if (_singleInstanceMutex != null)
                    {
                        try { _singleInstanceMutex.ReleaseMutex(); } catch { /* not owned */ }
                        _singleInstanceMutex.Dispose();
                    }
                    _showInstanceEvent?.Dispose();
                }

                Log.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Error("Error during shutdown", ex);
            }

            base.OnExit(e);
        }
    }
}