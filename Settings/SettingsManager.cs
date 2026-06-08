using System;
using System.Windows;
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

        private SettingsManager()
        {
            _settings = UserSettings.Instance;
        }

        #region Window Dimensions

        public Size GetMainWindowSize(ISessionDataProvider? sessionDataProvider = null)
        {
            double dynamicHeight = CalculateDynamicMainWindowHeight(sessionDataProvider);
            double baseWidth = MAIN_WINDOW_WIDTH_LARGE;

            double scaleFactor = GetMainWindowScaleFactor(_settings.WindowSize);
            double scaledWidth = baseWidth * scaleFactor;
            double scaledHeight = dynamicHeight * scaleFactor;

            return new Size(scaledWidth, scaledHeight);
        }

        public Size GetRadarWindowSize()
        {
            return GetBaseDimensions(_settings.WindowSize, isMainWindow: false);
        }

        private double CalculateDynamicMainWindowHeight(ISessionDataProvider? sessionDataProvider = null)
        {
            double totalHeight = WINDOW_PADDING;

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
                return new Point(_settings.MainWindowX, _settings.MainWindowY);
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
                return new Point(_settings.RadarWindowX, _settings.RadarWindowY);
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

    public enum SettingsChangeType
    {
        ElementVisibility,
        WindowSize,
        WindowPosition
    }

    #endregion
}