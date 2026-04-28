using System;
using System.Reflection;
using System.Windows;
using VISOR.Diagnostics;
using VISOR.Telemetry;
using VISOR.Views;
using VISOR.Settings;

namespace VISOR
{
    public partial class App : Application
    {
        private SVappsLABSDKWrapper _sdkWrapper;
        private MainWindow _mainWindow;
        private RadarWindow _radarWindow;
        private ConfigWindow _configWindow;

        public RadarWindow CurrentRadarWindow => _radarWindow;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

                _mainWindow.Show();
                _radarWindow?.Show();
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

        private void OnMainWindowClosed(object sender, EventArgs e)
        {
            Log.Info("MainWindow closed - shutting down application");
            Shutdown();
        }

        private void OnRadarWindowClosed(object sender, EventArgs e)
        {
            Log.Info("RadarWindow closed independently");
        }

        private void OnConfigWindowClosed(object sender, EventArgs e)
        {
            Log.Info("ConfigWindow closed independently");
        }

        private void OnConfigExitRequested(object sender, EventArgs e)
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