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

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                System.Diagnostics.Debug.WriteLine("=== App OnStartup started ===");

                // Create and initialize the shared SDK wrapper
                System.Diagnostics.Debug.WriteLine("Creating SDK wrapper...");
                _sdkWrapper = new SVappsLABSDKWrapper();

                System.Diagnostics.Debug.WriteLine("Initializing SDK wrapper...");
                bool initialized = await _sdkWrapper.Initialize();
                System.Diagnostics.Debug.WriteLine($"SDK initialization result: {initialized}");

                if (!initialized)
                {
                    MessageBox.Show("Failed to initialize telemetry connection.", "Initialization Error");
                    Shutdown();
                    return;
                }

                // Launch all windows together
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

                // Create main window first
                System.Diagnostics.Debug.WriteLine("Creating MainWindow...");
                _mainWindow = new MainWindow(_sdkWrapper);
                System.Diagnostics.Debug.WriteLine("MainWindow created successfully");

                // Create radar window (check if it should be visible)
                var settingsManager = new SettingsManager();
                if (settingsManager.IsRadarVisible)
                {
                    System.Diagnostics.Debug.WriteLine("Creating RadarWindow with shared ClassColorManager...");
                    _radarWindow = new RadarWindow(_sdkWrapper, _mainWindow.ViewModel.ClassColorManager);
                    System.Diagnostics.Debug.WriteLine("RadarWindow created successfully");
                }

                // Create config window with references to the other windows
                System.Diagnostics.Debug.WriteLine("Creating ConfigWindow...");
                _configWindow = new ConfigWindow(_sdkWrapper, _mainWindow, _radarWindow);
                System.Diagnostics.Debug.WriteLine("ConfigWindow created successfully");

                // Show all windows
                System.Diagnostics.Debug.WriteLine("Showing MainWindow...");
                _mainWindow.Show();

                if (_radarWindow != null)
                {
                    System.Diagnostics.Debug.WriteLine("Showing RadarWindow...");
                    _radarWindow.Show();
                }

                System.Diagnostics.Debug.WriteLine("Showing ConfigWindow...");
                _configWindow.Show();

                // Set main window as the primary window for shutdown behavior
                System.Diagnostics.Debug.WriteLine("Setting MainWindow as primary...");
                MainWindow = _mainWindow;

                // Handle window closing events
                _mainWindow.Closed += OnMainWindowClosed;

                if (_radarWindow != null)
                {
                    _radarWindow.Closed += OnRadarWindowClosed;
                }

                _configWindow.Closed += OnConfigWindowClosed;

                // Handle config window requesting application exit
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
            // If radar window is closed but main window is still open, don't shutdown
            // This allows users to close just the radar if they want
        }

        private void OnConfigWindowClosed(object sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("ConfigWindow closed independently");
            // Config window can be closed without affecting main application
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
                // Clean shutdown of SDK wrapper
                _sdkWrapper?.Shutdown();
            }
            catch (Exception ex)
            {
                // Log but don't prevent shutdown
                System.Diagnostics.Debug.WriteLine($"Error during shutdown: {ex.Message}");
            }

            base.OnExit(e);
        }
    }
}