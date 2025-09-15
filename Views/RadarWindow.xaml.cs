using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VISOR.ViewModels;
using VISOR.Telemetry;

namespace VISOR.Views
{
    public partial class RadarWindow : Window
    {
        private readonly RadarViewModel _viewModel;
        private readonly SVappsLABSDKWrapper _sdk;

        public RadarWindow(SVappsLABSDKWrapper sdkWrapper)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            _viewModel = new RadarViewModel();
            DataContext = _viewModel;

            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            Background = new SolidColorBrush(Color.FromArgb(160, 32, 32, 32));
            Topmost = true;

            Loaded += RadarWindow_Loaded;

            // Position in upper right quadrant with center-offset logic
            double centerX = SystemParameters.PrimaryScreenWidth / 2;
            double centerY = SystemParameters.PrimaryScreenHeight / 2;
            double offsetX = 400; // Right side
            double offsetY = -200; // Upper portion
            Left = centerX + offsetX - (Width / 2);
            Top = centerY + offsetY - (Height / 2);

            // Ensure window stays on screen
            if (Left + Width > SystemParameters.PrimaryScreenWidth)
                Left = SystemParameters.PrimaryScreenWidth - Width - 20;
            if (Top < 0)
                Top = 20;

            // Drag anywhere
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private async void RadarWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Subscribe to events
                _sdk.SnapshotAvailable += OnSnapshotAvailable;
                _sdk.ConnectionStateChanged += OnConnectionStateChanged;
                _sdk.PrimedStateChanged += OnPrimedStateChanged;

                // Check initial state
                UpdateUIState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Radar window error: {ex.Message}");
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
                    DebugText.Text = "Radar: Disconnected";
                }
                else
                {
                    DebugText.Text = "Radar: Connected";
                }
            });
        }

        private void OnPrimedStateChanged(bool isPrimed)
        {
            Dispatcher.Invoke(() =>
            {
                if (isPrimed)
                {
                    DebugText.Text = "Radar: Active";
                }
                else
                {
                    DebugText.Text = "Radar: Waiting for session data";
                    _viewModel.Reset();
                }
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            Dispatcher.Invoke(() =>
            {
                if (_sdk.IsSessionDataReady)
                {
                    _viewModel.UpdateFromTelemetry(snapshot, _sdk.Coordinator, CarsContainer);
                    DebugText.Text = $"Cars: {_viewModel.VisibleCarCount}";
                }
                else
                {
                    _viewModel.Reset();
                    DebugText.Text = "Radar: No session data";
                }
            });
        }

        private void UpdateUIState()
        {
            Dispatcher.Invoke(() =>
            {
                if (_sdk.IsPrimed)
                {
                    DebugText.Text = "Radar: Active";
                }
                else if (_sdk.IsConnected)
                {
                    DebugText.Text = "Radar: Connected, waiting for session data";
                }
                else
                {
                    DebugText.Text = "Radar: Waiting for iRacing";
                }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            // Unsubscribe from events
            if (_sdk != null)
            {
                _sdk.SnapshotAvailable -= OnSnapshotAvailable;
                _sdk.ConnectionStateChanged -= OnConnectionStateChanged;
                _sdk.PrimedStateChanged -= OnPrimedStateChanged;
            }
            base.OnClosed(e);
        }
    }
}