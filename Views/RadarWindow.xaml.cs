using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VISOR.Diagnostics;
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
        private readonly WindowSdkLink _sdkLink;

        private int _lastVisibleCarCount = 0;
        private bool _isFadedOut = false;
        private bool _forceVisible = false;

        // The radar's design size, which RadarWindow.xaml's grid lines are drawn at.
        private const double DesignWidth = 240.0;
        private const double DesignHeight = 396.0;

        // Each grid line as RadarWindow.xaml draws it, recorded before the first resize. Every
        // resize places the lines from these, never from where the last resize left them.
        private readonly (Line Line, bool Vertical, double DesignX, double DesignY)[] _gridLines;

        public RadarWindow(SVappsLABSDKWrapper sdkWrapper, ClassColorManager classColorManager)
        {
            InitializeComponent();

            _sdk = sdkWrapper;
            _settingsManager = SettingsManager.Instance;
            _gridLines = RadarCanvas.Children.OfType<Line>()
                .Select(line => (line, Math.Abs(line.X1 - line.X2) < 0.1, line.X1, line.Y1))
                .ToArray();

            _viewModel = new RadarViewModel(classColorManager);
            DataContext = _viewModel;
            _sdkLink = new WindowSdkLink(this, _sdk, ProcessSnapshot, OnConnectionChanged, OnPrimedChanged);

            ApplyWindowSizing();
            ApplyWindowPositioning();

            _settingsManager.WindowSizeChanged += OnWindowSizeChanged;

            // A radar switched on from the Config window is created while config mode is already
            // on, so keep it visible, as the Config window does for an existing radar. Its drag
            // handle reads the current mode itself (ConfigDragHandle).
            SetForceVisible(ConfigModeManager.Instance.IsInConfigMode);

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
            double scaleFactor = width / DesignWidth;

            RadarCanvas.Width = width;
            RadarCanvas.Height = height;

            double zoneWidth = width / 5;

            UpdateRadarLines(width, height);
            UpdateZoneHighlights(height, zoneWidth);
            UpdatePlayerCar(height, scaleFactor, zoneWidth);
            UpdateZoneLabelsGrid(width, zoneWidth);
        }

        // Zone separators keep their share of the width (a fifth each) and run full height; the
        // centre line and distance markers keep their share of the height and run full width.
        private void UpdateRadarLines(double width, double height)
        {
            foreach (var (line, vertical, designX, designY) in _gridLines)
            {
                if (vertical)
                {
                    line.X1 = line.X2 = designX / DesignWidth * width;
                    line.Y2 = height;
                }
                else
                {
                    line.Y1 = line.Y2 = designY / DesignHeight * height;
                    line.X2 = width;
                }
            }
        }

        private void UpdateZoneHighlights(double height, double zoneWidth)
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

        private void UpdatePlayerCar(double height, double scaleFactor, double zoneWidth)
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

        private void UpdateZoneLabelsGrid(double width, double zoneWidth)
        {
            var grid = RadarCanvas.Children.OfType<Grid>().FirstOrDefault();
            if (grid != null)
            {
                grid.Width = width;

                for (int i = 0; i < grid.ColumnDefinitions.Count && i < 5; i++)
                {
                    grid.ColumnDefinitions[i].Width = new GridLength(zoneWidth);
                }
            }
        }

        private void OnWindowSizeChanged(object? sender, WindowSizeChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                Log.Debug($"[RadarWindow] Window size preset changed to {e.NewSize} - resizing");
                ApplyWindowSizing();
            });
        }

        private void RadarWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                _sdkLink.Attach();
                UpdateUIState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Radar window error: {ex.Message}");
                Close();
            }
        }

        // UI thread, queued by the SDK link.
        private void OnConnectionChanged(bool isConnected)
        {
            if (!isConnected)
            {
                ClearRadar("Radar: Disconnected", "Offline");
                UpdatePlayerCarDisplay();
            }
            else
            {
                DebugText.Text = "Radar: Connected";
                CarLeftRightIndicator.Text = "Connecting";
            }
        }

        // UI thread, queued by the SDK link.
        private void OnPrimedChanged(bool isPrimed)
        {
            if (isPrimed)
            {
                DebugText.Text = "Radar: Active";
                CarLeftRightIndicator.Text = "Ready";
            }
            else
            {
                ClearRadar("Radar: Waiting for session data", "Waiting");
                UpdatePlayerCarDisplay();
            }
        }

        // UI thread, queued in order by the SDK link's frame poster.
        private void ProcessSnapshot(SVappsLABSnapshot snapshot)
        {
            // One view for the whole frame: every reader below sees the same session update.
            var session = _sdk.Coordinator.Current;
            if (session.IsDataReady)
            {
                // Alone on track in lone qualifying: nothing to show.
                if (session.IsLoneQualifying)
                {
                    ClearRadar("Radar: Hidden (Lone Qualifying)", "Hidden");
                    return;
                }

                UpdatePlayerCarDisplay(snapshot, session);

                _viewModel.UpdateFromTelemetry(snapshot, session);

                UpdateZoneHighlights(snapshot);

                UpdateFadeState(_viewModel.VisibleCarCount);

                DebugText.Text = $"Cars: {_viewModel.VisibleCarCount}";
            }
            else
            {
                ClearRadar("Radar: No session data", "No Data");
            }
        }

        // No cars to draw (disconnected, waiting, lone qualifying, no session data): empty the
        // radar, drop the highlights and fade out, showing why.
        private void ClearRadar(string status, string indicator)
        {
            _viewModel.Clear();
            ResetZoneHighlights();
            FadeOut();
            DebugText.Text = status;
            CarLeftRightIndicator.Text = indicator;
        }

        private void UpdatePlayerCarDisplay(SVappsLABSnapshot? snapshot = null, SessionView? session = null)
        {
            if (snapshot == null || session == null || !session.IsDataReady)
            {
                PlayerCarNumber.Text = "??";
                return;
            }

            var playerCarIdx = snapshot.PlayerCarIdx;
            if (playerCarIdx >= 0)
            {
                var carNumbers = session.CarNumbers;
                if (carNumbers != null && carNumbers.Length > playerCarIdx)
                {
                    PlayerCarNumber.Text = carNumbers[playerCarIdx] ?? "??";
                }
            }
        }

        private void UpdateZoneHighlights(SVappsLABSnapshot snapshot)
        {
            var carLeftRight = snapshot.CarLeftRightState;

            ResetZoneHighlights();

            CarLeftRightIndicator.Text = carLeftRight;

            const double highlightOpacity = 0.15;

            foreach (var zone in RadarViewModel.SideZonesFor(carLeftRight))
                HighlightFor(zone).Opacity = highlightOpacity;
        }

        // Zone 1 is the outer ("far") lane on each side, zone 2 the inner ("near") one.
        private UIElement HighlightFor(RadarZone zone) => zone switch
        {
            RadarZone.LeftFar => LeftZone1Highlight,
            RadarZone.LeftNear => LeftZone2Highlight,
            RadarZone.RightNear => RightZone2Highlight,
            RadarZone.RightFar => RightZone1Highlight,
            _ => CenterZoneHighlight,
        };

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
            AnimateOpacity(0.0, 500);
        }

        private void FadeIn()
        {
            if (!_isFadedOut) return;
            _isFadedOut = false;
            AnimateOpacity(1.0, 300);
        }

        private void AnimateOpacity(double to, int milliseconds)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = Opacity,
                To = to,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            // The SDK link and the drag handle detach themselves when the window closes.
            _settingsManager.WindowSizeChanged -= OnWindowSizeChanged;

            base.OnClosed(e);
        }
    }
}