using System.Windows;
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
        private readonly WindowSdkLink _sdkLink;
        private bool _lastRelativeVisibility = true;

        public MainViewModel ViewModel => _viewModel;

        public MainWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            _settingsManager = SettingsManager.Instance;
            _viewModel = new MainViewModel();
            DataContext = _viewModel;
            _sdkLink = new WindowSdkLink(this, _sdk, ProcessSnapshot, OnConnectionChanged, OnPrimedChanged);

            ApplyWindowSizing();
            ApplyWindowPositioning();

            _settingsManager.ElementVisibilityChanged += OnElementVisibilityChanged;
            _settingsManager.WindowSizeChanged += OnWindowSizeChanged;

            Loaded += MainWindow_Loaded;
        }

        private void ApplyWindowSizing()
        {
            var session = _sdk.Coordinator.Current;
            bool relativeHidden = session.IsDataReady && session.IsLoneQualifying;

            var windowSize = _settingsManager.GetMainWindowSize(relativeHidden);
            Width = windowSize.Width;
            Height = windowSize.Height;
        }

        private void ApplyWindowPositioning()
        {
            var windowPosition = _settingsManager.GetMainWindowPosition();
            Left = windowPosition.X;
            Top = windowPosition.Y;
        }

        private void OnElementVisibilityChanged(object? sender, EventArgs e) =>
            RefreshLayout("[MainWindow] Element visibility changed - resizing window");

        private void OnWindowSizeChanged(object? sender, WindowSizeChangedEventArgs e) =>
            RefreshLayout($"[MainWindow] Window size preset changed to {e.NewSize} - resizing");

        private void RefreshLayout(string reason)
        {
            Dispatcher.Invoke(() =>
            {
                Log.Debug(reason);
                _viewModel.RefreshElementVisibility();
                ApplyWindowSizing();
            });
        }

        private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                _sdkLink.Attach();
                UpdateUIState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
                Close();
            }
        }

        // UI thread, queued by the SDK link.
        private void OnConnectionChanged(bool isConnected)
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
        }

        // UI thread, queued by the SDK link.
        private async void OnPrimedChanged(bool isPrimed)
        {
            if (isPrimed)
            {
                StatusText.Text = "Fully connected!";

                // Back on the UI thread after the delay. Leave the text up if the HUD has
                // dropped back to waiting in the meantime.
                await Task.Delay(1000);
                if (_sdk.IsPrimed)
                    StatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusText.Text = "Waiting for session data...";
                StatusText.Visibility = Visibility.Visible;
            }
        }

        // UI thread, queued in order by the SDK link's frame poster.
        private void ProcessSnapshot(SVappsLABSnapshot snapshot)
        {
            // One view for the whole frame: every reader below sees the same session update.
            var session = _sdk.Coordinator.Current;
            if (session.IsDataReady)
            {
                _viewModel.UpdateFromTelemetry(snapshot, session);

                // The relative hides in lone qualifying.
                bool currentRelativeVisibility = !session.IsLoneQualifying;
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
            if (_sdk.IsPrimed)
            {
                StatusText.Visibility = Visibility.Collapsed;
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

            base.OnClosed(e);
        }
    }
}