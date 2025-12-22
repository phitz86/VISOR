using System;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VISOR.Diagnostics;
using VISOR.Settings;
using VISOR.Telemetry;
using VISOR.ViewModels;
using VISOR.Views;

namespace VISOR.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly SVappsLABSDKWrapper _sdk;
        private readonly SettingsManager _settingsManager;
        private readonly ConfigModeManager _configModeManager;
        private static DateTime _lastSessionReadyLog = DateTime.MinValue;
        private bool _isDragging = false;
        private bool _lastRelativeVisibility = true;

        public MainViewModel ViewModel => _viewModel;

        public MainWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            _settingsManager = SettingsManager.Instance;
            _configModeManager = ConfigModeManager.Instance;
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            Background = Brushes.Transparent;
            Topmost = true;

            ApplyWindowSizing();
            ApplyWindowPositioning();

            _settingsManager.ElementVisibilityChanged += OnElementVisibilityChanged;
            _settingsManager.WindowSizeChanged += OnWindowSizeChanged;
            _configModeManager.ConfigModeChanged += OnConfigModeChanged;

            Loaded += MainWindow_Loaded;
        }

        private void ApplyWindowSizing()
        {
            ISessionDataProvider sessionProvider = null;
            if (_sdk?.IsSessionDataReady == true)
            {
                sessionProvider = _sdk.Coordinator;
            }

            var windowSize = _settingsManager.GetMainWindowSize(sessionProvider);
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
                Log.Info("[MainWindow] Element visibility changed - resizing window");
                _viewModel.RefreshElementVisibility();
                ApplyWindowSizing();
            });
        }

        private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Log.Info($"[MainWindow] Window size preset changed to {e.NewSize} - resizing");
                _viewModel.RefreshElementVisibility();
                ApplyWindowSizing();
                Log.Debug($"[MainWindow] Applied dimensions: {Width}x{Height}");
            });
        }

        private void OnConfigModeChanged(object sender, ConfigModeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Log.Info($"[MainWindow] Config mode changed to: {e.IsInConfigMode}");
                DragHandle.Visibility = e.IsInConfigMode ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_configModeManager.IsInConfigMode)
            {
                _isDragging = true;
                DragMove();
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _sdk.SnapshotAvailable += OnSnapshotAvailable;
                _sdk.SessionYamlAvailable += OnSessionYamlAvailable;
                _sdk.ConnectionStateChanged += OnConnectionStateChanged;
                _sdk.PrimedStateChanged += OnPrimedStateChanged;
                _sdk.SessionDataUpdated += OnSessionDataUpdated;

                UpdateUIState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
                Close();
            }
        }

        private void OnConnectionStateChanged(bool isConnected)
        {
            try
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
            catch (TaskCanceledException) { }
        }

        private void OnPrimedStateChanged(bool isPrimed)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (isPrimed)
                    {
                        StatusText.Text = "Fully connected!";
                        Task.Delay(1000).ContinueWith(_ =>
                        {
                            try
                            {
                                Dispatcher.Invoke(() => StatusText.Visibility = Visibility.Collapsed);
                            }
                            catch (TaskCanceledException) { /* Ignore shutdown */ }
                            catch (OperationCanceledException) { /* Ignore shutdown */ }
                        });
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
            catch (TaskCanceledException) { /* Ignore shutdown */ }
        }

        private void OnSessionDataUpdated()
        {
            Dispatcher.Invoke(() =>
            {
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (_sdk.IsSessionDataReady)
                    {
                        var now = DateTime.Now;
                        if ((now - _lastSessionReadyLog).TotalSeconds > 10)
                        {
                            _lastSessionReadyLog = now;
                        }

                        _viewModel.UpdateFromTelemetry(snapshot, _sdk.Coordinator);

                        bool currentRelativeVisibility = !_sdk.Coordinator.ShouldHideRelativeDisplay();
                        if (currentRelativeVisibility != _lastRelativeVisibility)
                        {
                            Log.Info($"[MainWindow] Relative display visibility changed: {_lastRelativeVisibility} -> {currentRelativeVisibility}");
                            ApplyWindowSizing();
                            _lastRelativeVisibility = currentRelativeVisibility;
                        }
                    }
                    else
                    {
                        Log.Debug("[MainWindow] Session data not ready - passing null");
                        _viewModel.UpdateFromTelemetry(snapshot, null);
                    }
                });
            }
            catch (TaskCanceledException) { }
        }

        private void OnSessionYamlAvailable(string yaml)
        {
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
                    // Non-fatal
                }
            });
        }

        private void UpdateUIState()
        {
            try
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
            catch (TaskCanceledException) { }
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ConfigButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (Window window in Application.Current.Windows)
            {
                if (window is ConfigWindow)
                {
                    window.Activate();
                    return;
                }
            }

            ConfigWindow configWindow = new ConfigWindow(_sdk, this);
            configWindow.Show();
        }

        protected override void OnClosed(EventArgs e)
        {
            _settingsManager.ElementVisibilityChanged -= OnElementVisibilityChanged;
            _settingsManager.WindowSizeChanged -= OnWindowSizeChanged;
            _configModeManager.ConfigModeChanged -= OnConfigModeChanged;

            base.OnClosed(e);
        }
    }
}