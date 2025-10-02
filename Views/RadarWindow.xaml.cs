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

        // Fade animation properties
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

            // Initialize window positioning and sizing using SettingsManager
            ApplyWindowSizing();
            ApplyWindowPositioning();

            System.Diagnostics.Debug.WriteLine($"[RadarWindow] Initialized with size {Width}x{Height} at position ({Left}, {Top})");

            // Subscribe to settings changes for real-time updates
            _settingsManager.WindowSizeChanged += OnWindowSizeChanged;

            // Subscribe to config mode changes
            _configModeManager.ConfigModeChanged += OnConfigModeChanged;

            Loaded += RadarWindow_Loaded;

            // Initialize player car number display
            UpdatePlayerCarDisplay();
        }

        /// <summary>
        /// Force the radar window to remain visible (for config preview)
        /// </summary>
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

            // Update main content container size to match window
            MainContentContainer.Width = windowSize.Width;
            MainContentContainer.Height = windowSize.Height;

            // Update canvas size to match window
            RadarCanvas.Width = windowSize.Width;
            RadarCanvas.Height = windowSize.Height;

            // Recalculate layout elements based on new size
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
            double scaleFactor = width / 240.0; // Base width is 240

            // Update canvas size to match window
            RadarCanvas.Width = width;
            RadarCanvas.Height = height;

            // Calculate scaled dimensions
            double zoneWidth = width / 5; // 5 zones
            double centerY = height / 2;

            // Update all hardcoded line elements in the XAML by finding them and updating
            UpdateRadarLines(width, height, scaleFactor);

            // Update zone highlights
            UpdateZoneHighlights(width, height, zoneWidth);

            // Update player car with proportional scaling
            UpdatePlayerCar(width, height, scaleFactor, zoneWidth);

            // Update zone labels grid
            UpdateZoneLabelsGrid(width, height);
        }

        private void UpdateRadarLines(double width, double height, double scaleFactor)
        {
            // Find and update vertical zone separator lines
            var lines = RadarCanvas.Children.OfType<Line>().ToList();

            foreach (var line in lines)
            {
                // Vertical lines (zone separators)
                if (Math.Abs(line.X1 - line.X2) < 0.1) // Vertical line
                {
                    double originalX = line.X1;
                    double newX = 0;

                    // Map original positions to new scaled positions
                    if (Math.Abs(originalX - 48) < 0.1) newX = width * 0.2;      // Zone 1|2 boundary
                    else if (Math.Abs(originalX - 96) < 0.1) newX = width * 0.4;  // Zone 2|3 boundary  
                    else if (Math.Abs(originalX - 144) < 0.1) newX = width * 0.6; // Zone 3|4 boundary
                    else if (Math.Abs(originalX - 192) < 0.1) newX = width * 0.8; // Zone 4|5 boundary

                    line.X1 = line.X2 = newX;
                    line.Y2 = height;
                }
                // Horizontal lines
                else if (Math.Abs(line.Y1 - line.Y2) < 0.1) // Horizontal line
                {
                    double originalY = line.Y1;

                    // Center line (player position)
                    if (Math.Abs(originalY - 198) < 5) // Center line (around 198 for 396 height)
                    {
                        line.Y1 = line.Y2 = height / 2;
                        line.X2 = width;
                    }
                    // Distance markers - scale proportionally
                    else
                    {
                        double relativeY = originalY / 396.0; // Original height was 396
                        line.Y1 = line.Y2 = relativeY * height;
                        line.X2 = width;
                    }
                }
            }
        }

        private void UpdateZoneHighlights(double width, double height, double zoneWidth)
        {
            // Update zone highlight rectangles
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
            // Scale player car proportionally
            double baseCarWidth = 24;
            double baseCarHeight = 36;
            double scaledCarWidth = baseCarWidth * scaleFactor;
            double scaledCarHeight = baseCarHeight * scaleFactor;

            // Position in center zone, middle of window
            double centerZoneLeft = zoneWidth * 2; // Zone index 2 (center)
            double carLeft = centerZoneLeft + (zoneWidth - scaledCarWidth) / 2;
            double carTop = (height - scaledCarHeight) / 2;

            PlayerCar.Width = scaledCarWidth;
            PlayerCar.Height = scaledCarHeight;
            Canvas.SetLeft(PlayerCar, carLeft);
            Canvas.SetTop(PlayerCar, carTop);

            // Update player car number text
            Canvas.SetLeft(PlayerCarNumber, carLeft);
            Canvas.SetTop(PlayerCarNumber, carTop + (scaledCarHeight * 0.1)); // Small offset from top
            PlayerCarNumber.Width = scaledCarWidth;
            PlayerCarNumber.Height = scaledCarHeight * 0.8;

            // Scale font size proportionally
            PlayerCarNumber.FontSize = Math.Max(8, 12 * scaleFactor); // Minimum 8px, scales from base 12px
        }

        private void UpdateZoneLabelsGrid(double width, double height)
        {
            // Find the zone labels grid within the canvas
            var grid = RadarCanvas.Children.OfType<Grid>().FirstOrDefault();
            if (grid != null)
            {
                // Update grid dimensions
                grid.Width = width;

                // Update column definitions to match new zone widths
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

                // Simply show/hide the drag handle based on config mode
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

        private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;

                // Save position now that drag is complete
                var mainWindow = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
                if (mainWindow != null)
                {
                    _settingsManager.SaveWindowPositions(
                        new Point(mainWindow.Left, mainWindow.Top),
                        new Point(this.Left, this.Top)
                    );
                    System.Diagnostics.Debug.WriteLine($"[RadarWindow] Position saved after drag: ({this.Left}, {this.Top})");
                }
            }
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
            // Don't fade if we're forcing visibility
            if (_forceVisible)
                return;

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
            if (_isFadedOut || _forceVisible) return;
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

            // Unsubscribe from settings events
            _settingsManager.WindowSizeChanged -= OnWindowSizeChanged;
            _configModeManager.ConfigModeChanged -= OnConfigModeChanged;

            base.OnClosed(e);
        }
    }
}