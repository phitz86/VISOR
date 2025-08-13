using System.Windows;
using VISOR.Telemetry;
using VISOR.Views;

namespace VISOR
{
    public partial class App : Application
    {
        private SVappsLABSDKWrapper? _sdkWrapper;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Create a single, shared instance of the SDK Wrapper.
            // This ensures both ConfigWindow and MainWindow use the same telemetry connection.
            _sdkWrapper = new SVappsLABSDKWrapper();

            // Start the telemetry connection early so it can be ready
            // for the "Dump YAML" feature in the ConfigWindow.
            _sdkWrapper.Start();

            // Create and show the ConfigWindow, passing it the shared SDK instance.
            var configWindow = new ConfigWindow(_sdkWrapper);
            configWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Ensure the telemetry connection is properly shut down when the app closes.
            _sdkWrapper?.Shutdown();
            base.OnExit(e);
        }
    }
}
