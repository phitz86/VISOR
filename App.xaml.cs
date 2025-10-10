using System;
using System.Windows;
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
                System.Diagnostics.Debug.WriteLine("=== App OnStartup started ===");

                _sdkWrapper = new SVappsLABSDKWrapper();

                bool initialized = await _sdkWrapper.Initialize();
                System.Diagnostics.Debug.WriteLine($"SDK initialization result: {initialized}");

                if (!initialized)
                {
                    MessageBox.Show("Failed to initialize telemetry connection.", "Initialization Error");
                    Shutdown();
                    return;
                }

                LaunchAllWindows();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== EXCEPTION in OnStartup: {ex} ===");
                MessageBox.Show($"Application startup error: {ex.Message}\n\nFull details:\n{ex}", "Startup Error");
                Shutdown();
            }
        }

        private void LaunchAllWindows()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== LaunchAllWindows started ===");

                _mainWindow = new MainWindow(_sdkWrapper);
                System.Diagnostics.Debug.WriteLine("MainWindow created successfully");

                var settingsManager = SettingsManager.Instance;
                if (settingsManager.IsRadarVisible)
                {
                    _radarWindow = new RadarWindow(_sdkWrapper, _mainWindow.ViewModel.ClassColorManager);
                    System.Diagnostics.Debug.WriteLine("RadarWindow created successfully");
                }

                _configWindow = new ConfigWindow(_sdkWrapper, _mainWindow);
                System.Diagnostics.Debug.WriteLine("ConfigWindow created successfully");

                System.Diagnostics.Debug.WriteLine("Showing MainWindow...");
                _mainWindow.Show();

                if (_radarWindow != null)
                {
                    System.Diagnostics.Debug.WriteLine("Showing RadarWindow...");
                    _radarWindow.Show();
                }

                System.Diagnostics.Debug.WriteLine("Showing ConfigWindow...");
                _configWindow.Show();

                MainWindow = _mainWindow;

                _mainWindow.Closed += OnMainWindowClosed;

                if (_radarWindow != null)
                {
                    _radarWindow.Closed += OnRadarWindowClosed;
                }

                _configWindow.Closed += OnConfigWindowClosed;
                _configWindow.ExitRequested += OnConfigExitRequested;

                System.Diagnostics.Debug.WriteLine("=== LaunchAllWindows completed successfully ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== EXCEPTION in LaunchAllWindows: {ex} ===");
                MessageBox.Show($"Error launching application windows: {ex.Message}\n\nFull details:\n{ex}", "Launch Error");
                Shutdown();
            }
        }

        private void OnMainWindowClosed(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow closed - shutting down application");
            Shutdown();
        }

        private void OnRadarWindowClosed(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("RadarWindow closed independently");
        }

        private void OnConfigWindowClosed(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("ConfigWindow closed independently");
        }

        private void OnConfigExitRequested(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Config window requested application exit");
            Shutdown();
        }

        public void ShowRadarWindow()
        {
            if (_radarWindow == null && _mainWindow != null)
            {
                System.Diagnostics.Debug.WriteLine("Creating RadarWindow on demand...");
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
                System.Diagnostics.Debug.WriteLine("=== App OnExit called ===");
                _sdkWrapper?.Shutdown();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error during shutdown: {ex.Message}");
            }

            base.OnExit(e);
        }
    }
}