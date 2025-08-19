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
        private readonly SessionDataParser _sessionParser; // Add a field for the parser

        public MainWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            _sessionParser = new SessionDataParser(); // Instantiate the parser
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

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
                _sdk.SnapshotAvailable += OnSnapshotAvailable;
                _sdk.SessionYamlAvailable += OnSessionYamlAvailable;
                _sdk.ConnectionStateChanged += OnConnectionStateChanged; // Subscribe to the new event

                await WaitForFirstSnapshotAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
                Close();
            }
        }

        private void OnConnectionStateChanged(bool isConnected)
        {
            // Run on the UI thread to safely update UI-bound properties.
            Dispatcher.Invoke(() =>
            {
                if (!isConnected)
                {
                    // If we disconnect, reset everything.
                    _viewModel.Reset();
                    _sessionParser.ClearCache();
                    StatusText.Text = "Disconnected. Waiting for iRacing...";
                    StatusText.Visibility = Visibility.Visible;

                    // Start the waiting process again.
                    _ = WaitForFirstSnapshotAsync();
                }
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            // Now pass both the snapshot and the parser to the ViewModel
            Dispatcher.Invoke(() => _viewModel.UpdateFromTelemetry(snapshot, _sessionParser));
        }

        private void OnSessionYamlAvailable(string yaml)
        {
            // The parser needs to be updated with the latest session info
            _sessionParser.ParseSessionData(yaml);

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
