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

            // Initialize player car number display
            UpdatePlayerCarDisplay();
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
                    CarLeftRightIndicator.Text = "Offline";
                    ResetZoneHighlights();
                    UpdatePlayerCarDisplay();
                }
                else
                {
                    DebugText.Text = "Radar: Connected";
                    CarLeftRightIndicator.Text = "Connecting";
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
                    CarLeftRightIndicator.Text = "Ready";
                }
                else
                {
                    DebugText.Text = "Radar: Waiting for session data";
                    CarLeftRightIndicator.Text = "Waiting";
                    _viewModel.Reset();
                    ResetZoneHighlights();
                    UpdatePlayerCarDisplay();
                }
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            Dispatcher.Invoke(() =>
            {
                if (_sdk.IsSessionDataReady)
                {
                    // Update player car display first
                    UpdatePlayerCarDisplay(snapshot);

                    // Update radar with new zone-based logic
                    _viewModel.UpdateFromTelemetry(snapshot, _sdk.Coordinator, CarsContainer);

                    // Update zone highlights based on CarLeftRight state
                    UpdateZoneHighlights(snapshot);

                    // Update debug info
                    DebugText.Text = $"Cars: {_viewModel.VisibleCarCount}";
                }
                else
                {
                    _viewModel.Reset();
                    ResetZoneHighlights();
                    DebugText.Text = "Radar: No session data";
                    CarLeftRightIndicator.Text = "No Data";
                }
            });
        }

        private void UpdatePlayerCarDisplay(SVappsLABSnapshot snapshot = null)
        {
            if (snapshot == null || !_sdk.IsSessionDataReady)
            {
                PlayerCarNumber.Text = "??";
                return;
            }

            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx", -1);
            if (playerCarIdx >= 0)
            {
                var carNumbers = _sdk.Coordinator.CarNumbers;
                if (carNumbers != null && carNumbers.Length > playerCarIdx)
                {
                    PlayerCarNumber.Text = carNumbers[playerCarIdx] ?? "??";
                }
            }
        }

        private void UpdateZoneHighlights(SVappsLABSnapshot snapshot)
        {
            // Get CarLeftRight state from telemetry
            var carLeftRightEnum = snapshot.GetValue<object>("CarLeftRight", null);
            var carLeftRight = carLeftRightEnum?.ToString() ?? "Off";

            // Reset all highlights first
            ResetZoneHighlights();

            // Update CarLeftRight indicator text
            CarLeftRightIndicator.Text = carLeftRight;

            // Apply zone highlights based on state
            const double highlightOpacity = 0.15;

            switch (carLeftRight)
            {
                case "CarLeft":
                    LeftZone2Highlight.Opacity = highlightOpacity;
                    break;

                case "CarRight":
                    RightZone2Highlight.Opacity = highlightOpacity;
                    break;

                case "CarLeftRight":
                    LeftZone2Highlight.Opacity = highlightOpacity;
                    RightZone2Highlight.Opacity = highlightOpacity;
                    break;

                case "TwoCarsLeft":
                    LeftZone1Highlight.Opacity = highlightOpacity;
                    LeftZone2Highlight.Opacity = highlightOpacity;
                    break;

                case "TwoCarsRight":
                    RightZone1Highlight.Opacity = highlightOpacity;
                    RightZone2Highlight.Opacity = highlightOpacity;
                    break;

                case "Clear": // Clear - no highlights needed
                case "Off": // Off - no highlights needed
                default:
                    // Already reset above
                    break;
            }
        }

        private void ResetZoneHighlights()
        {
            LeftZone1Highlight.Opacity = 0;
            LeftZone2Highlight.Opacity = 0;
            CenterZoneHighlight.Opacity = 0;
            RightZone1Highlight.Opacity = 0;
            RightZone2Highlight.Opacity = 0;
        }

        private void UpdateUIState()
        {
            Dispatcher.Invoke(() =>
            {
                if (_sdk.IsPrimed)
                {
                    DebugText.Text = "Radar: Active";
                    CarLeftRightIndicator.Text = "Ready";
                }
                else if (_sdk.IsConnected)
                {
                    DebugText.Text = "Radar: Connected, waiting for session data";
                    CarLeftRightIndicator.Text = "Waiting";
                }
                else
                {
                    DebugText.Text = "Radar: Waiting for iRacing";
                    CarLeftRightIndicator.Text = "Offline";
                }

                UpdatePlayerCarDisplay();
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