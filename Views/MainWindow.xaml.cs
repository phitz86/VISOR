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

        public MainWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
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
                _sdk.PrimedStateChanged += OnPrimedStateChanged;
                _sdk.SessionDataUpdated += OnSessionDataUpdated;

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
                Console.WriteLine("[MainWindow] Session data updated - drivers may have changed");
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            Dispatcher.Invoke(() =>
            {
                // Access the wrapper's session parser directly - no need to create our own
                if (_sdk.IsSessionDataReady)
                {
                    // Create a simple wrapper to provide the session parser interface
                    // that the ViewModels expect, using the wrapper's parsed data
                    var sessionDataWrapper = new SessionDataWrapper(_sdk);
                    _viewModel.UpdateFromTelemetry(snapshot, sessionDataWrapper);
                }
                else
                {
                    // Just update the basic telemetry without session-dependent features
                    _viewModel.UpdateFromTelemetry(snapshot, null);
                }
            });
        }

        private void OnSessionYamlAvailable(string yaml)
        {
            // Save YAML for debugging purposes
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
            int retries = 0;

            while (!_sdk.IsConnected && retries < 360) // Wait up to 3 minutes for connection
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

    /// <summary>
    /// Simple wrapper to provide SessionDataParser interface using the SDK wrapper's data
    /// </summary>
    public class SessionDataWrapper : VISOR.ViewModels.ISessionDataProvider
    {
        private readonly SVappsLABSDKWrapper _wrapper;

        public SessionDataWrapper(SVappsLABSDKWrapper wrapper)
        {
            _wrapper = wrapper;
        }

        public bool IsDataReady => _wrapper.IsSessionDataReady;

        public string[] UserNames => _wrapper.GetUserNames();
        public string[] CarNumbers => _wrapper.GetCarNumbers();
        public int[] CarNumberRaw => _wrapper.GetCarNumberRaw();
        public int[] CarClassIDs => _wrapper.GetCarClassIDs();
        public bool[] CarIsAI => _wrapper.GetCarIsAI();
        public int[] CurDriverIncidentCount => _wrapper.GetCurDriverIncidentCount();
    }
}