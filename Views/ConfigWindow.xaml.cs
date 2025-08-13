using System.Windows;
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

        private void DumpYaml_Click(object sender, RoutedEventArgs e)
        {
            var path = _telemetry.DumpLatestYaml();
            MessageBox.Show(path == "NO_YAML" ? "No session YAML data is available yet. Make sure iRacing is running." : $"YAML data saved to: {path}");
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
