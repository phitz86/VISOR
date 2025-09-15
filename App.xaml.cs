using System;
using System.Windows;
using VISOR.Telemetry;
using VISOR.Views;

namespace VISOR
{
    public partial class App : Application
    {
        private SVappsLABSDKWrapper _sdkWrapper;

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

                // Show the ConfigWindow first
                System.Diagnostics.Debug.WriteLine("Creating ConfigWindow...");
                var configWindow = new ConfigWindow(_sdkWrapper);

                System.Diagnostics.Debug.WriteLine("Showing ConfigWindow as dialog...");
                var configResult = configWindow.ShowDialog();
                System.Diagnostics.Debug.WriteLine($"ConfigWindow dialog result: {configResult}");

                if (configResult == true)
                {
                    System.Diagnostics.Debug.WriteLine("ConfigWindow returned true - launching main application...");
                    // Config window closed with OK - launch main application windows
                    LaunchMainApplication();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("ConfigWindow returned false/null - shutting down...");
                    // Config window was cancelled - shutdown
                    Shutdown();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== EXCEPTION in OnStartup: {ex} ===");
                MessageBox.Show($"Application startup error: {ex.Message}\n\nFull details:\n{ex}", "Startup Error");
                Shutdown();
            }
        }

        private void LaunchMainApplication()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== LaunchMainApplication started ===");

                // Create and show main window
                System.Diagnostics.Debug.WriteLine("Creating MainWindow...");
                var mainWindow = new MainWindow(_sdkWrapper);
                System.Diagnostics.Debug.WriteLine("MainWindow created successfully");

                System.Diagnostics.Debug.WriteLine("Showing MainWindow...");
                mainWindow.Show();
                System.Diagnostics.Debug.WriteLine("MainWindow shown successfully");

                // Create and show radar window
                System.Diagnostics.Debug.WriteLine("Creating RadarWindow...");
                var radarWindow = new RadarWindow(_sdkWrapper);
                System.Diagnostics.Debug.WriteLine("RadarWindow created successfully");

                System.Diagnostics.Debug.WriteLine("Showing RadarWindow...");
                radarWindow.Show();
                System.Diagnostics.Debug.WriteLine("RadarWindow shown successfully");

                // Set main window as the primary window for shutdown behavior
                System.Diagnostics.Debug.WriteLine("Setting MainWindow as primary...");
                MainWindow = mainWindow;

                // Ensure both windows close together
                mainWindow.Closed += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine("MainWindow closed - closing RadarWindow and shutting down");
                    radarWindow?.Close();
                    Shutdown();
                };

                radarWindow.Closed += (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine("RadarWindow closed independently");
                    // If radar window is closed but main window is still open, don't shutdown
                    // This allows users to close just the radar if they want
                };

                System.Diagnostics.Debug.WriteLine("=== LaunchMainApplication completed successfully ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"=== EXCEPTION in LaunchMainApplication: {ex} ===");
                MessageBox.Show($"Error launching application windows: {ex.Message}\n\nFull details:\n{ex}", "Launch Error");
                Shutdown();
            }
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