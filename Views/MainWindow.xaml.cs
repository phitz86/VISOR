using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VISOR.Diagnostics;
using VISOR.Settings;
using VISOR.Telemetry;
using VISOR.ViewModels;

namespace VISOR.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly SVappsLABSDKWrapper _sdk;
        private readonly SettingsManager _settingsManager;
        private readonly ConfigModeManager _configModeManager;
        private readonly FramePoster _framePoster;
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
            _framePoster = new FramePoster(Dispatcher, "MainWindow", ProcessSnapshot);

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
            ISessionDataProvider? sessionProvider = null;
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

        private void OnElementVisibilityChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Log.Debug("[MainWindow] Element visibility changed - resizing window");
                _viewModel.RefreshElementVisibility();
                ApplyWindowSizing();
            });
        }

        private void OnWindowSizeChanged(object? sender, WindowSizeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Log.Debug($"[MainWindow] Window size preset changed to {e.NewSize} - resizing");
                _viewModel.RefreshElementVisibility();
                ApplyWindowSizing();
            });
        }

        private void OnConfigModeChanged(object? sender, ConfigModeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Log.Debug($"[MainWindow] Config mode changed to: {e.IsInConfigMode}");
                DragHandle.Visibility = e.IsInConfigMode ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private void DragHandle_MouseLeftButtonDown(object? sender, MouseButtonEventArgs e)
        {
            if (_configModeManager.IsInConfigMode)
            {
                DragMove();
            }
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                _sdk.SnapshotAvailable += OnSnapshotAvailable;
                _sdk.ConnectionStateChanged += OnConnectionStateChanged;
                _sdk.PrimedStateChanged += OnPrimedStateChanged;

                UpdateUIState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
                Close();
            }
        }

        // Raised on an SDK thread. Queued rather than invoked, so the SDK never waits on the UI
        // thread, which during shutdown is itself waiting for the SDK to stop.
        private void OnConnectionStateChanged(bool isConnected)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
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
            }));
        }

        private async void OnPrimedStateChanged(bool isPrimed)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (isPrimed)
                    {
                        StatusText.Text = "Fully connected!";
                        _viewModel.IsTelemetryConnected = true;
                    }
                    else
                    {
                        StatusText.Text = "Waiting for session data...";
                        StatusText.Visibility = Visibility.Visible;
                        _viewModel.IsTelemetryConnected = false;
                    }
                });

                if (isPrimed)
                {
                    await Task.Delay(1000);
                    await Dispatcher.InvokeAsync(() => StatusText.Visibility = Visibility.Collapsed);
                }
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
        }

        // SDK telemetry thread: queue the frame for the UI thread, in order (see FramePoster).
        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot) => _framePoster.Post(snapshot);

        // UI thread.
        private void ProcessSnapshot(SVappsLABSnapshot snapshot)
        {
            if (_sdk.IsSessionDataReady)
            {
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

        private void ExitButton_Click(object? sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void ConfigButton_Click(object? sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ShowConfigWindow();
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