using System;
using System.Windows;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.Settings
{
    public class SettingsManager
    {
        private const double MAIN_WINDOW_WIDTH_LARGE = 640.0;
        private const double MAIN_WINDOW_HEIGHT_LARGE = 640.0;
        private const double RADAR_WINDOW_WIDTH_LARGE = 240.0;
        private const double RADAR_WINDOW_HEIGHT_LARGE = 396.0;

        private const double ROW_HEIGHT_POSITION_GEAR = 100.0;
        private const double ROW_HEIGHT_TIME_FUEL = 60.0;
        private const double ROW_HEIGHT_DELTA_BAR = 40.0;
        private const double ROW_HEIGHT_LAP_TIMES = 50.0;
        private const double ROW_HEIGHT_RELATIVE = 250.0;
        private const double ROW_HEIGHT_WARNINGS = 60.0;
        private const double WINDOW_PADDING = 20.0;

        // The overlay's rounded-corner Border (MainWindow.xaml, Margin="10") frames the
        // scaled content grid, so its 10px-per-side chrome is applied in window space and
        // does NOT scale with the size preset. It must be added to the window height as a
        // constant. The old code folded this into the scaled padding budget instead, which
        // shrank it to ~14px at the 0.6x Small preset and clipped the bottom of Row 5.
        private const double BORDER_CHROME_HEIGHT = 20.0;

        private readonly UserSettings _settings;
        private static SettingsManager _instance = null!;

        public static SettingsManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SettingsManager();
                return _instance;
            }
        }

        public event EventHandler<SettingsChangedEventArgs>? SettingsChanged;
        public event EventHandler<WindowSizeChangedEventArgs>? WindowSizeChanged;
        public event EventHandler<ElementVisibilityChangedEventArgs>? ElementVisibilityChanged;
        public event EventHandler<RadarVisibilityChangedEventArgs>? RadarVisibilityChanged;
        public event EventHandler<PositionDisplayModeChangedEventArgs>? PositionDisplayModeChanged;

        private SettingsManager()
        {
            _settings = UserSettings.Instance;
        }

        #region Window Dimensions

        public Size GetMainWindowSize(ISessionDataProvider? sessionDataProvider = null)
        {
            double contentHeight = CalculateDynamicMainWindowHeight(sessionDataProvider);
            double baseWidth = MAIN_WINDOW_WIDTH_LARGE;

            double scaleFactor = GetMainWindowScaleFactor(_settings.WindowSize);
            double scaledWidth = baseWidth * scaleFactor;

            // Scale the content, then add the unscaled Border chrome. At Large this equals
            // the previous (contentHeight + WINDOW_PADDING) * 1.0, so Large/Medium are
            // visually unchanged; Small gains back the ~8px it was being clipped by.
            double scaledHeight = contentHeight * scaleFactor + BORDER_CHROME_HEIGHT;

            return new Size(scaledWidth, scaledHeight);
        }

        public Size GetRadarWindowSize()
        {
            return GetBaseDimensions(_settings.WindowSize, isMainWindow: false);
        }

        /// <summary>
        /// Sum of the visible rows plus one WINDOW_PADDING of interior breathing room,
        /// unscaled. This is the content the scaled grid needs; the window's fixed Border
        /// chrome is added separately (and unscaled) in <see cref="GetMainWindowSize"/>.
        /// </summary>
        private double CalculateDynamicMainWindowHeight(ISessionDataProvider? sessionDataProvider = null)
        {
            double totalHeight = 0.0;

            if (_settings.ShowRow0)
                totalHeight += ROW_HEIGHT_POSITION_GEAR;

            if (_settings.ShowRow1)
                totalHeight += ROW_HEIGHT_TIME_FUEL;

            if (_settings.ShowRow2)
                totalHeight += ROW_HEIGHT_DELTA_BAR;

            if (_settings.ShowRow3)
                totalHeight += ROW_HEIGHT_LAP_TIMES;

            bool showRelativeEffectively = _settings.ShowRow4;
            if (showRelativeEffectively && sessionDataProvider != null)
            {
                var coordinator = sessionDataProvider as SessionDataCoordinator;
                if (coordinator != null)
                {
                    int currentSession = coordinator.CurrentSessionNum;
                    bool isLoneQualifying = coordinator.IsLoneQualifying(currentSession);
                    if (isLoneQualifying)
                    {
                        showRelativeEffectively = false;
                    }
                }
            }

            if (showRelativeEffectively)
                totalHeight += ROW_HEIGHT_RELATIVE;

            if (_settings.ShowRow5)
                totalHeight += ROW_HEIGHT_WARNINGS;

            totalHeight += WINDOW_PADDING;

            return Math.Max(totalHeight, 200.0);
        }

        private Size GetBaseDimensions(WindowSizePreset sizePreset, bool isMainWindow)
        {
            if (isMainWindow)
            {
                return sizePreset switch
                {
                    WindowSizePreset.Small => new Size(MAIN_WINDOW_WIDTH_LARGE * 0.6, MAIN_WINDOW_HEIGHT_LARGE * 0.6),
                    WindowSizePreset.Medium => new Size(MAIN_WINDOW_WIDTH_LARGE * 0.8, MAIN_WINDOW_HEIGHT_LARGE * 0.8),
                    _ => new Size(MAIN_WINDOW_WIDTH_LARGE, MAIN_WINDOW_HEIGHT_LARGE)
                };
            }

            return sizePreset switch
            {
                WindowSizePreset.Small => new Size(RADAR_WINDOW_WIDTH_LARGE * 0.8, RADAR_WINDOW_HEIGHT_LARGE * 0.8),
                WindowSizePreset.Medium => new Size(RADAR_WINDOW_WIDTH_LARGE * 0.9, RADAR_WINDOW_HEIGHT_LARGE * 0.9),
                _ => new Size(RADAR_WINDOW_WIDTH_LARGE, RADAR_WINDOW_HEIGHT_LARGE)
            };
        }

        private double GetMainWindowScaleFactor(WindowSizePreset sizePreset)
        {
            return sizePreset switch
            {
                WindowSizePreset.Small => 0.6,
                WindowSizePreset.Medium => 0.8,
                _ => 1.0
            };
        }

        #endregion

        #region Window Positioning

        public Point GetMainWindowPosition()
        {
            if (_settings.MainWindowX >= 0 && _settings.MainWindowY >= 0)
            {
                var saved = new Point(_settings.MainWindowX, _settings.MainWindowY);
                return ClampToVisibleArea(saved, GetMainWindowSize());
            }

            double centerX = SystemParameters.PrimaryScreenWidth / 2;
            double centerY = SystemParameters.PrimaryScreenHeight / 2;
            double offsetX = 900;
            double offsetY = 400;

            var windowSize = GetMainWindowSize();
            double left = centerX + offsetX - (windowSize.Width / 2);
            double top = centerY + offsetY - (windowSize.Height / 2);

            return new Point(left, top);
        }

        public Point GetRadarWindowPosition()
        {
            if (_settings.RadarWindowX >= 0 && _settings.RadarWindowY >= 0)
            {
                var saved = new Point(_settings.RadarWindowX, _settings.RadarWindowY);
                return ClampToVisibleArea(saved, GetRadarWindowSize());
            }

            double centerX = SystemParameters.PrimaryScreenWidth / 2;
            double centerY = SystemParameters.PrimaryScreenHeight / 2;
            double offsetX = 400;
            double offsetY = -200;

            var windowSize = GetRadarWindowSize();
            double left = centerX + offsetX - (windowSize.Width / 2);
            double top = centerY + offsetY - (windowSize.Height / 2);

            if (left + windowSize.Width > SystemParameters.PrimaryScreenWidth)
                left = SystemParameters.PrimaryScreenWidth - windowSize.Width - 20;
            if (top < 0)
                top = 20;

            return new Point(left, top);
        }

        /// <summary>
        /// Ensures a saved window position still lands on the current virtual desktop.
        /// If a monitor was removed or the resolution changed, a previously-saved
        /// position can be entirely off-screen, leaving the overlay invisible and
        /// unreachable. This pulls the window back so a usable portion stays visible.
        /// </summary>
        private static Point ClampToVisibleArea(Point position, Size windowSize)
        {
            // VirtualScreen* spans the bounding box of all connected monitors.
            double virtualLeft = SystemParameters.VirtualScreenLeft;
            double virtualTop = SystemParameters.VirtualScreenTop;
            double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
            double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

            // Keep at least this many pixels of the window on-screen so the user can
            // always grab and reposition it.
            const double minVisible = 80.0;

            double x = position.X;
            double y = position.Y;

            // Off the right/bottom: pull back so the whole window fits when possible.
            if (x > virtualRight - minVisible)
                x = virtualRight - windowSize.Width;
            if (y > virtualBottom - minVisible)
                y = virtualBottom - windowSize.Height;

            // Off the left/top: snap to the edge of the virtual desktop.
            if (x + windowSize.Width < virtualLeft + minVisible)
                x = virtualLeft;
            if (y + windowSize.Height < virtualTop + minVisible)
                y = virtualTop;

            if (x != position.X || y != position.Y)
            {
                Log.Info($"Saved window position ({position.X},{position.Y}) was off-screen; " +
                         $"clamped to ({x},{y})");
            }

            return new Point(x, y);
        }

        public void SaveWindowPositions(Point mainWindowPos, Point radarWindowPos)
        {
            _settings.MainWindowX = (int)mainWindowPos.X;
            _settings.MainWindowY = (int)mainWindowPos.Y;
            _settings.RadarWindowX = (int)radarWindowPos.X;
            _settings.RadarWindowY = (int)radarWindowPos.Y;
            _settings.SaveSettings();
        }

        #endregion

        #region Element Visibility

        public bool IsRadarVisible => _settings.ShowRadar;

        public void UpdateElementVisibility(bool showRow0, bool showRow1, bool showRow2,
            bool showRow3, bool showRow4, bool showRow5, bool showRadar)
        {
            var oldRadarVisible = _settings.ShowRadar;

            _settings.ShowRow0 = showRow0;
            _settings.ShowRow1 = showRow1;
            _settings.ShowRow2 = showRow2;
            _settings.ShowRow3 = showRow3;
            _settings.ShowRow4 = showRow4;
            _settings.ShowRow5 = showRow5;
            _settings.ShowRadar = showRadar;
            _settings.SaveSettings();

            ElementVisibilityChanged?.Invoke(this, new ElementVisibilityChangedEventArgs
            {
                ShowPositionAndGear = showRow0,
                ShowTimeAndFuel = showRow1,
                ShowLapDelta = showRow2,
                ShowLapTimes = showRow3,
                ShowRelative = showRow4,
                ShowWarnings = showRow5
            });

            if (oldRadarVisible != showRadar)
            {
                RadarVisibilityChanged?.Invoke(this, new RadarVisibilityChangedEventArgs { IsVisible = showRadar });
            }

            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { ChangeType = SettingsChangeType.ElementVisibility });
        }

        /// <summary>
        /// Persists the three Row 5 element toggles and signals a visibility change so the
        /// overlay re-reads them. Height is unaffected (Row 5 keeps its slot whenever
        /// ShowRow5 is on), so this reuses the element-visibility path.
        /// </summary>
        public void UpdateRow5ElementVisibility(bool showTrackLocation, bool showIncident, bool showTrackTemp)
        {
            _settings.ShowTrackLocation = showTrackLocation;
            _settings.ShowIncidentCounter = showIncident;
            _settings.ShowTrackTemp = showTrackTemp;
            _settings.SaveSettings();

            ElementVisibilityChanged?.Invoke(this, new ElementVisibilityChangedEventArgs
            {
                ShowPositionAndGear = _settings.ShowRow0,
                ShowTimeAndFuel = _settings.ShowRow1,
                ShowLapDelta = _settings.ShowRow2,
                ShowLapTimes = _settings.ShowRow3,
                ShowRelative = _settings.ShowRow4,
                ShowWarnings = _settings.ShowRow5
            });

            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { ChangeType = SettingsChangeType.ElementVisibility });
        }

        public void UpdatePositionDisplayMode(PositionDisplayMode newMode)
        {
            var oldMode = _settings.PositionDisplayMode;
            _settings.PositionDisplayMode = newMode;
            _settings.SaveSettings();

            if (oldMode != newMode)
            {
                PositionDisplayModeChanged?.Invoke(this, new PositionDisplayModeChangedEventArgs { NewMode = newMode });
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { ChangeType = SettingsChangeType.PositionDisplayMode });
            }
        }

        public void UpdateWindowSize(WindowSizePreset newSize)
        {
            var oldSize = _settings.WindowSize;
            _settings.WindowSize = newSize;
            _settings.SaveSettings();

            if (oldSize != newSize)
            {
                WindowSizeChanged?.Invoke(this, new WindowSizeChangedEventArgs
                {
                    NewSize = newSize,
                    NewMainWindowSize = GetMainWindowSize(),
                    NewRadarWindowSize = GetRadarWindowSize()
                });

                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { ChangeType = SettingsChangeType.WindowSize });
            }
        }

        #endregion

        #region Settings Access

        public UserSettings Settings => _settings;

        #endregion
    }

    #region Event Args

    public class SettingsChangedEventArgs : EventArgs
    {
        public SettingsChangeType ChangeType { get; set; }
    }

    public class WindowSizeChangedEventArgs : EventArgs
    {
        public WindowSizePreset NewSize { get; set; }
        public Size NewMainWindowSize { get; set; }
        public Size NewRadarWindowSize { get; set; }
    }

    public class ElementVisibilityChangedEventArgs : EventArgs
    {
        public bool ShowPositionAndGear { get; set; }
        public bool ShowTimeAndFuel { get; set; }
        public bool ShowLapDelta { get; set; }
        public bool ShowLapTimes { get; set; }
        public bool ShowRelative { get; set; }
        public bool ShowWarnings { get; set; }
    }

    public class RadarVisibilityChangedEventArgs : EventArgs
    {
        public bool IsVisible { get; set; }
    }

    public class PositionDisplayModeChangedEventArgs : EventArgs
    {
        public PositionDisplayMode NewMode { get; set; }
    }

    public enum SettingsChangeType
    {
        ElementVisibility,
        WindowSize,
        WindowPosition,
        PositionDisplayMode
    }

    #endregion
}