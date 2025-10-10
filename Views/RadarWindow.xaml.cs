using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VISOR.Settings;
using VISOR.Telemetry;
using VISOR.ViewModels;

namespace VISOR.Views
{
    public partial class RadarWindow : Window
    {
        private readonly RadarViewModel _viewModel;
        private readonly SVappsLABSDKWrapper _sdk;
        private readonly SettingsManager _settingsManager;
        private readonly ConfigModeManager _configModeManager;

        private int _lastVisibleCarCount = 0;
        private bool _isFadedOut = false;
        private bool _forceVisible = false;
        private bool _isDragging = false;

        public RadarWindow(SVappsLABSDKWrapper sdkWrapper, ClassColorManager classColorManager)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            _settingsManager = SettingsManager.Instance;
            _configModeManager = ConfigModeManager.Instance;
            _viewModel = new RadarViewModel(classColorManager);
            DataContext = _viewModel;

            AllowsTransparency = true;
            WindowStyle = WindowStyle.None;
            Background = Brushes.Transparent;
            Topmost = true;

            ApplyWindowSizing();
            ApplyWindowPositioning();

            _settingsManager.WindowSizeChanged += OnWindowSizeChanged;
            _configModeManager.ConfigModeChanged += OnConfigModeChanged;

            Loaded += RadarWindow_Loaded;

            UpdatePlayerCarDisplay();
        }

        public void SetForceVisible(bool forceVisible)
        {
            _forceVisible = forceVisible;

            if (forceVisible && _isFadedOut)
            {
                FadeIn();
            }
        }

        private void ApplyWindowSizing()
        {
            var windowSize = _settingsManager.GetRadarWindowSize();
            Width = windowSize.Width;
            Height = windowSize.Height;

            MainContentContainer.Width = windowSize.Width;
            MainContentContainer.Height = windowSize.Height;

            RadarCanvas.Width = windowSize.Width;
            RadarCanvas.Height = windowSize.Height;

            UpdateLayoutForNewSize(windowSize);
        }

        private void ApplyWindowPositioning()
        {
            var windowPosition = _settingsManager.GetRadarWindowPosition();
            Left = windowPosition.X;
            Top = windowPosition.Y;
        }

        private void UpdateLayoutForNewSize(Size newSize)
        {
            double width = newSize.Width;
            double height = newSize.Height;
            double scaleFactor = width / 240.0;

            RadarCanvas.Width = width;
            RadarCanvas.Height = height;

            double zoneWidth = width / 5;
            double centerY = height / 2;

            UpdateRadarLines(width, height, scaleFactor);
            UpdateZoneHighlights(width, height, zoneWidth);
            UpdatePlayerCar(width, height, scaleFactor, zoneWidth);
            UpdateZoneLabelsGrid(width, height);
        }

        private void UpdateRadarLines(double width, double height, double scaleFactor)
        {
            var lines = RadarCanvas.Children.OfType<Line>().ToList();

            foreach (var line in lines)
            {
                if (Math.Abs(line.X1 - line.X2) < 0.1)
                {
                    double originalX = line.X1;
                    double newX = 0;

                    if (Math.Abs(originalX - 48) < 0.1) newX = width * 0.2;
                    else if (Math.Abs(originalX - 96) < 0.1) newX = width * 0.4;
                    else if (Math.Abs(originalX - 144) < 0.1) newX = width * 0.6;
                    else if (Math.Abs(originalX - 192) < 0.1) newX = width * 0.8;

                    line.X1 = line.X2 = newX;
                    line.Y2 = height;
                }
                else if (Math.Abs(line.Y1 - line.Y2) < 0.1)
                {
                    double originalY = line.Y1;

                    if (Math.Abs(originalY - 198) < 5)
                    {
                        line.Y1 = line.Y2 = height / 2;
                        line.X2 = width;
                    }
                    else
                    {
                        double relativeY = originalY / 396.0;
                        line.Y1 = line.Y2 = relativeY * height;
                        line.X2 = width;
                    }
                }
            }
        }

        private void UpdateZoneHighlights(double width, double height, double zoneWidth)
        {
            var highlights = new[] { LeftZone1Highlight, LeftZone2Highlight, CenterZoneHighlight, RightZone2Highlight, RightZone1Highlight };

            for (int i = 0; i < highlights.Length; i++)
            {
                var highlight = highlights[i];
                highlight.Width = zoneWidth;
                highlight.Height = height;
                Canvas.SetLeft(highlight, i * zoneWidth);
                Canvas.SetTop(highlight, 0);
            }
        }

        private void UpdatePlayerCar(double width, double height, double scaleFactor, double zoneWidth)
        {
            double baseCarWidth = 24;
            double baseCarHeight = 36;
            double scaledCarWidth = baseCarWidth * scaleFactor;
            double scaledCarHeight = baseCarHeight * scaleFactor;

            double centerZoneLeft = zoneWidth * 2;
            double carLeft = centerZoneLeft + (zoneWidth - scaledCarWidth) / 2;
            double carTop = (height - scaledCarHeight) / 2;

            PlayerCar.Width = scaledCarWidth;
            PlayerCar.Height = scaledCarHeight;
            Canvas.SetLeft(PlayerCar, carLeft);
            Canvas.SetTop(PlayerCar, carTop);

            Canvas.SetLeft(PlayerCarNumber, carLeft);
            Canvas.SetTop(PlayerCarNumber, carTop + (scaledCarHeight * 0.1));
            PlayerCarNumber.Width = scaledCarWidth;
            PlayerCarNumber.Height = scaledCarHeight * 0.8;

            PlayerCarNumber.FontSize = Math.Max(8, 12 * scaleFactor);
        }

        private void UpdateZoneLabelsGrid(double width, double height)
        {
            var grid = RadarCanvas.Children.OfType<Grid>().FirstOrDefault();
            if (grid != null)
            {
                grid.Width = width;

                double zoneWidth = width / 5;
                for (int i = 0; i < grid.ColumnDefinitions.Count && i < 5; i++)
                {
                    grid.ColumnDefinitions[i].Width = new GridLength(zoneWidth);
                }
            }
        }

        private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[RadarWindow] Window size preset changed to {e.NewSize} - resizing");
                ApplyWindowSizing();
            });
        }

        private void OnConfigModeChanged(object sender, ConfigModeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[RadarWindow] Config mode changed to: {e.IsInConfigMode}");

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

        private async void RadarWindow_Loaded(object sender, RoutedEventArgs e)
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
                    FadeOut();
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
                    FadeOut();
                }
            });
        }

        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot)
        {
            Dispatcher.Invoke(() =>
            {
                if (_sdk.IsSessionDataReady)
                {
                    if (ShouldHideRadar())
                    {
                        _viewModel.Reset();
                        ResetZoneHighlights();
                        FadeOut();
                        DebugText.Text = "Radar: Hidden (Lone Qualifying)";
                        CarLeftRightIndicator.Text = "Hidden";
                        return;
                    }

                    UpdatePlayerCarDisplay(snapshot);

                    _viewModel.UpdateFromTelemetry(snapshot, _sdk.Coordinator, CarsContainer);

                    UpdateZoneHighlights(snapshot);

                    UpdateFadeState(_viewModel.VisibleCarCount);

                    DebugText.Text = $"Cars: {_viewModel.VisibleCarCount}";
                }
                else
                {
                    _viewModel.Reset();
                    ResetZoneHighlights();
                    FadeOut();
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
            var carLeftRightEnum = snapshot.GetValue<object>("CarLeftRight", null);
            var carLeftRight = carLeftRightEnum?.ToString() ?? "Off";

            ResetZoneHighlights();

            CarLeftRightIndicator.Text = carLeftRight;

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

                case "Clear":
                case "Off":
                default:
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
                    FadeOut();
                }
                else
                {
                    DebugText.Text = "Radar: Waiting for iRacing";
                    CarLeftRightIndicator.Text = "Offline";
                    FadeOut();
                }

                UpdatePlayerCarDisplay();
            });
        }

        private void UpdateFadeState(int visibleCarCount)
        {
            if (_forceVisible)
                return;

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
            if (_isFadedOut || _forceVisible) return;
            _isFadedOut = true;

            var fadeOut = new DoubleAnimation
            {
                From = this.Opacity,
                To = 0.0,
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
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            this.BeginAnimation(Window.OpacityProperty, fadeIn);
        }

        private bool ShouldHideRadar()
        {
            int currentSession = _sdk.Coordinator.CurrentSessionNum;
            return _sdk.Coordinator.IsLoneQualifying(currentSession);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_sdk != null)
            {
                _sdk.SnapshotAvailable -= OnSnapshotAvailable;
                _sdk.ConnectionStateChanged -= OnConnectionStateChanged;
                _sdk.PrimedStateChanged -= OnPrimedStateChanged;
            }

            _settingsManager.WindowSizeChanged -= OnWindowSizeChanged;
            _configModeManager.ConfigModeChanged -= OnConfigModeChanged;

            base.OnClosed(e);
        }
    }
}