using System.Windows;
using System.Windows.Controls;
using VISOR.Telemetry;

namespace VISOR.Views
{
    public partial class ConfigWindow : Window
    {
        private readonly SVappsLABSDKWrapper _telemetry;

        public ConfigWindow(SVappsLABSDKWrapper telemetry)
        {
            InitializeComponent();
            _telemetry = telemetry;
        }

        private void DumpYamlButton_Click(object sender, RoutedEventArgs e)
        {
            // --- Add this line ---
            VISOR.Diagnostics.ConnectionDiagnostics.RunDiagnostics();

            // The rest of your code for this method...
            var sdk = ((App)Application.Current).SdkWrapper;
            var dumpPath = sdk.DumpLatestYaml();
            MessageBox.Show($"Session YAML dumped to: {dumpPath}");
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            // Create the MainWindow, passing the shared telemetry instance to it.
            var mainWindow = new MainWindow(_telemetry);
            mainWindow.Show();

            // Close the configuration window.
            this.Close();
        }
    }
}
