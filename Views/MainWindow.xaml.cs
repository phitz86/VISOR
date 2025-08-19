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
        // REMOVED: Don't create our own SessionDataParser!

        public MainWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            // REMOVED: _sessionParser = new SessionDataParser();
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
                // Subscribe to events
                _sdk.SnapshotAvailable += OnSnapshotAvailable;
                _sdk.SessionYamlAvailable += OnSessionYamlAvailable;
                _sdk.ConnectionStateChanged += OnConnectionStateChanged;
                _sdk.PrimedStateChanged += OnPrimedStateChanged; // NEW: Subscribe to primed state
                _sdk.SessionDataUpdated += OnSessionDataUpdated; // NEW: Subscribe to session updates

                // Check initial state
                UpdateUIState();

                // If not primed, show waiting message
                if (!_sdk.IsPrimed)
                {
                    await WaitForPrimedStateAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
                Close();
            }
        }

        private void OnConnectionStateChanged(bool isConnected)
        {
            Dispatcher.Invoke(() =>
            {
                if (!isConnected)
                {
                    _viewModel.Reset();
                    StatusText.Text = "Disconnected. Waiting for iRacing...";
                    StatusText.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusText.Text = "Connected, waiting for session data...";
                    StatusText.Visibility = Visibility.Visible;
                }
            });
        }

        private void OnPrimedStateChanged(bool isPrimed)
        {
            Dispatcher.Invoke(() =>
            {
                if (isPrimed)
                {
                    StatusText.Text = "Fully connected!";
                    StatusText.Visibility = Visibility.Collapsed;
                    _viewModel.IsTelemetryConnected = true;
                }
                else
                {
                    StatusText.Text = "Waiting for session data...";
                    StatusText.Visibility = Visibility.Visible;
                    _viewModel.IsTelemetryConnected = false;
                }
            });
        }

        private void OnSessionDataUpdated()
        {
            // Session data changed (driver joined/left)
            Dispatcher.Invoke(() =>
            {
                // Could show a brief notification if desired
                Console.WriteLine("[MainWindow] Session data updated - drivers may have changed");
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            // IMPORTANT: We no longer pass a separate SessionDataParser
            // The snapshot already contains the parsed session data from the wrapper's internal parser
            Dispatcher.Invoke(() =>
            {
                // Get the session data arrays from the snapshot
                var carNumbers = snapshot.GetValue<int[]>("CarIdxCarNumber");
                var carClasses = snapshot.GetValue<string[]>("CarIdxClass");

                // Create a temporary parser just for the ViewModels to use
                // This is a workaround - ideally ViewModels would work directly with snapshot data
                var tempParser = new SessionDataParser();
                if (!string.IsNullOrEmpty(snapshot.RawSessionData))
                {
                    tempParser.ParseSessionData(snapshot.RawSessionData);
                }

                _viewModel.UpdateFromTelemetry(snapshot, tempParser);
            });
        }

        private void OnSessionYamlAvailable(string yaml)
        {
            // REMOVED: Don't parse it ourselves - the wrapper already did!
            // Just save it for debugging if needed
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
                    // Non-fatal, can be ignored
                }
            });
        }

        private async Task WaitForPrimedStateAsync()
        {
            // Just wait for telemetry connection, not session data
            int retries = 0;

            while (!_sdk.IsConnected && retries < 360) // Wait up to 3 minutes for connection only
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

            if (_sdk.IsConnected)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Connected!";
                    // Hide after a brief moment
                    Task.Delay(1000).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() => StatusText.Visibility = Visibility.Collapsed);
                    });
                });
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = "Timeout waiting for telemetry.";
                });
            }
        }

        private void UpdateUIState()
        {
            Dispatcher.Invoke(() =>
            {
                if (_sdk.IsPrimed)
                {
                    StatusText.Visibility = Visibility.Collapsed;
                    _viewModel.IsTelemetryConnected = true;
                }
                else if (_sdk.IsConnected)
                {
                    StatusText.Text = "Connected, waiting for session data...";
                    StatusText.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusText.Text = "Waiting for iRacing...";
                    StatusText.Visibility = Visibility.Visible;
                }
            });
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}