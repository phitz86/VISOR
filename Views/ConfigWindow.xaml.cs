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
            MessageBox.Show(path == "NO_YAML" ? "No session YAML yet." : $"Saved: {path}");
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement launch functionality
            // This would typically create and show the main overlay window
            // with the selected configuration options
        }
    }
}