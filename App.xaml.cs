using System.Windows;
using VISOR.Telemetry; // Make sure you have this using statement
using VISOR.Views;    // And this one

namespace VISOR
{
    public partial class App : Application
    {
        // 1. Add the public property to hold the shared wrapper instance.
        public SVappsLABSDKWrapper SdkWrapper { get; private set; }

        // 2. Override the OnStartup method to control the launch sequence.
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Create and initialize the shared SDK wrapper
            SdkWrapper = new SVappsLABSDKWrapper();
            await SdkWrapper.Initialize();

            // Now, show the ConfigWindow
            var configWindow = new ConfigWindow(SdkWrapper);
            configWindow.Show();
        }
    }
}