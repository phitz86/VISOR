using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VISOR.ViewModels;
using VISOR.Telemetry;
using VISOR.Settings;
using VISOR.Views;

namespace VISOR.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly SVappsLABSDKWrapper _sdk;
        private readonly SettingsManager _settingsManager;
        private static DateTime _lastSessionReadyLog = DateTime.MinValue;

        // Expose ViewModel for App.xaml.cs to access ClassColorManager
        public MainViewModel ViewModel => _viewModel;

        public MainWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            _settingsManager = new SettingsManager();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            Background = new SolidColorBrush(Color.FromArgb(160, 32, 32, 32));
            Topmost = true;

            // Initialize window positioning and sizing using SettingsManager
            ApplyWindowSizing();
            ApplyWindowPositioning();

            System.Diagnostics.Debug.WriteLine($"[MainWindow] Initialized with size {Width}x{Height} at position ({Left}, {Top})");

            // Subscribe to settings changes for real-time updates
            _settingsManager.ElementVisibilityChanged += OnElementVisibilityChanged;
            _settingsManager.WindowSizeChanged += OnWindowSizeChanged;

            Loaded += MainWindow_Loaded;

            // Save position when window is moved
            this.LocationChanged += MainWindow_LocationChanged;

            // Drag anywhere
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private void ApplyWindowSizing()
        {
            var windowSize = _settingsManager.GetMainWindowSize();
            Width = windowSize.Width;
            Height = windowSize.Height;
        }

        private void ApplyWindowPositioning()
        {
            var windowPosition = _settingsManager.GetMainWindowPosition();
            Left = windowPosition.X;
            Top = windowPosition.Y;
        }

        private void OnElementVisibilityChanged(object sender, ElementVisibilityChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine("[MainWindow] Element visibility changed - resizing window");

                // Update ViewModel to refresh visibility bindings
                _viewModel.RefreshElementVisibility();

                // Resize window to fit new content
                ApplyWindowSizing();
            });
        }

        private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Window size preset changed to {e.NewSize} - resizing");

                // Update ViewModel to refresh ScaleFactor binding
                _viewModel.RefreshElementVisibility();

                // Apply new window dimensions
                ApplyWindowSizing();
            });
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            // Only save position if window is fully loaded and not during initialization
            if (this.IsLoaded)
            {
                // Find radar window to save both positions together
                var radarWindow = Application.Current.Windows.OfType<RadarWindow>().FirstOrDefault();
                if (radarWindow != null)
                {
                    _settingsManager.SaveWindowPositions(
                        new Point(this.Left, this.Top),
                        new Point(radarWindow.Left, radarWindow.Top)
                    );
                }
            }
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
                if (_sdk.IsSessionDataReady)
                {
                    // Throttle debug output to once per second
                    var now = DateTime.Now;
                    if ((now - _lastSessionReadyLog).TotalSeconds > 10)
                    {
                        System.Diagnostics.Debug.WriteLine("[MainWindow] Session data ready - passing SDK's Coordinator");
                        _lastSessionReadyLog = now;
                    }

                    _viewModel.UpdateFromTelemetry(snapshot, _sdk.Coordinator);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[MainWindow] Session data not ready - passing null");
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

        protected override void OnClosed(EventArgs e)
        {
            // Unsubscribe from settings events
            _settingsManager.ElementVisibilityChanged -= OnElementVisibilityChanged;
            _settingsManager.WindowSizeChanged -= OnWindowSizeChanged;

            base.OnClosed(e);
        }
    }
}