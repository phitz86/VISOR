using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VISOR.ViewModels;
using VISOR.Telemetry;

namespace VISOR.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly SVappsLABSDKWrapper _sdk;

        // The constructor now accepts the shared SDK wrapper instance.
        public MainWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper; // Use the passed-in instance.
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            // Window chrome / translucency
            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            Background = new SolidColorBrush(Color.FromArgb(160, 32, 32, 32));
            Topmost = true;

            Loaded += MainWindow_Loaded;

            // Position near center with offset
            double centerX = SystemParameters.PrimaryScreenWidth / 2;
            double centerY = SystemParameters.PrimaryScreenHeight / 2;
            double offsetX = 900;
            double offsetY = 400;
            Left = centerX + offsetX - (Width / 2);
            Top = centerY + offsetY - (Height / 2);

            // Drag anywhere
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (StatusText != null)
                {
                    StatusText.Text = "Connecting...";
                    StatusText.Visibility = Visibility.Visible;
                }

                // The SDK is already started, so we just need to subscribe to its events.
                _sdk.SnapshotAvailable += OnSnapshotAvailable;
                _sdk.SessionYamlAvailable += OnSessionYamlAvailable;

                await WaitForFirstSnapshotAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
                Close();
            }
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            // Keep UI updates on the dispatcher thread for safety.
            Dispatcher.Invoke(() => _viewModel.UpdateFromTelemetry(snapshot));
        }

        private void OnSessionYamlAvailable(string yaml)
        {
            // This can run in the background.
            Task.Run(() =>
            {
                try
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "telemetry_baselines", "SVapps");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "session.yaml"), yaml);
                }
                catch
                {
                    // Non-fatal, can be ignored for now.
                }
            });
        }

        private async Task WaitForFirstSnapshotAsync()
        {
            int retries = 0;
            bool gotData = false;

            void Handler(SVappsLABSnapshot s) => gotData = true;
            _sdk.SnapshotAvailable += Handler;

            while (!gotData && retries < 360) // Wait for up to 3 minutes
            {
                if (StatusText != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "Waiting for iRacing telemetry...";
                        StatusText.Visibility = Visibility.Visible;
                    });
                }

                await Task.Delay(500);
                retries++;
            }

            _sdk.SnapshotAvailable -= Handler;

            if (!gotData)
            {
                if (StatusText != null)
                {
                    Dispatcher.Invoke(() => { StatusText.Text = "No telemetry connection found."; });
                }
                return;
            }

            if (StatusText != null)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Connected.";
                    StatusText.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
