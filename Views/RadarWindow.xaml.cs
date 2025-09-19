using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VISOR.ViewModels;
using VISOR.Telemetry;

namespace VISOR.Views
{
    public partial class RadarWindow : Window
    {
        private readonly RadarViewModel _viewModel;
        private readonly SVappsLABSDKWrapper _sdk;

        // Fade animation properties
        private int _lastVisibleCarCount = 0;
        private bool _isFadedOut = false;

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
                    FadeOut(); // Fade out when disconnected
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
                    // Don't fade in here - let car count drive the fade state
                }
                else
                {
                    DebugText.Text = "Radar: Waiting for session data";
                    CarLeftRightIndicator.Text = "Waiting";
                    _viewModel.Reset();
                    ResetZoneHighlights();
                    UpdatePlayerCarDisplay();
                    FadeOut(); // Fade out when not primed
                }
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            Dispatcher.Invoke(() =>
            {
                if (_sdk.IsSessionDataReady)
                {
                    // Check if we should hide radar (lone qualifying)
                    if (ShouldHideRadar())
                    {
                        _viewModel.Reset();
                        ResetZoneHighlights();
                        FadeOut();
                        DebugText.Text = "Radar: Hidden (Lone Qualifying)";
                        CarLeftRightIndicator.Text = "Hidden";
                        return;
                    }

                    // Update player car display first
                    UpdatePlayerCarDisplay(snapshot);

                    // Update radar with new zone-based logic
                    _viewModel.UpdateFromTelemetry(snapshot, _sdk.Coordinator, CarsContainer);

                    // Update zone highlights based on CarLeftRight state
                    UpdateZoneHighlights(snapshot);

                    // Update fade state based on visible car count
                    UpdateFadeState(_viewModel.VisibleCarCount);

                    // Update debug info
                    DebugText.Text = $"Cars: {_viewModel.VisibleCarCount}";
                }
                else
                {
                    _viewModel.Reset();
                    ResetZoneHighlights();
                    FadeOut(); // Fade out when no session data
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
                    FadeOut(); // Fade out when waiting for data
                }
                else
                {
                    DebugText.Text = "Radar: Waiting for iRacing";
                    CarLeftRightIndicator.Text = "Offline";
                    FadeOut(); // Fade out when offline
                }

                UpdatePlayerCarDisplay();
            });
        }

        private void UpdateFadeState(int visibleCarCount)
        {
            // Only update fade state if car count actually changed
            if (visibleCarCount == _lastVisibleCarCount) return;

            _lastVisibleCarCount = visibleCarCount;

            if (visibleCarCount == 0 && !_isFadedOut)
            {
                FadeOut();
            }
            else if (visibleCarCount > 0 && _isFadedOut)
            {
                FadeIn();
            }
        }

        private void FadeOut()
        {
            if (_isFadedOut) return;
            _isFadedOut = true;

            var fadeOut = new DoubleAnimation
            {
                From = this.Opacity,
                To = 0.0, // Fade to completely invisible
                Duration = TimeSpan.FromMilliseconds(500),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            this.BeginAnimation(Window.OpacityProperty, fadeOut);
        }

        private void FadeIn()
        {
            if (!_isFadedOut) return;
            _isFadedOut = false;

            var fadeIn = new DoubleAnimation
            {
                From = this.Opacity,
                To = 1.0, // Fade to full opacity
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            this.BeginAnimation(Window.OpacityProperty, fadeIn);
        }

        private bool ShouldHideRadar()
        {
            // Use the same session checking logic as the Relative display
            int currentSession = _sdk.Coordinator.CurrentSessionNum;
            return _sdk.Coordinator.IsLoneQualifying(currentSession);
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