using System;
using System.Windows;

namespace VISOR.Settings
{
    /// <summary>
    /// Manages application settings and provides window sizing calculations.
    /// Acts as a bridge between UserSettings and the application windows.
    /// Singleton pattern ensures all windows share the same instance for proper event handling.
    /// </summary>
    public class SettingsManager
    {
        // Base dimensions (Large size - current dimensions)
        private const double MAIN_WINDOW_WIDTH_LARGE = 750.0;
        private const double MAIN_WINDOW_HEIGHT_LARGE = 640.0;
        private const double RADAR_WINDOW_WIDTH_LARGE = 240.0;
        private const double RADAR_WINDOW_HEIGHT_LARGE = 396.0;

        // Row heights for dynamic sizing (generous estimates to prevent cutoff)
        private const double ROW_HEIGHT_POSITION_GEAR = 100.0;     // Row 0: Large gear + position
        private const double ROW_HEIGHT_TIME_FUEL = 60.0;         // Row 1: Time + Fuel
        private const double ROW_HEIGHT_DELTA_BAR = 40.0;         // Row 2: Delta bar with margin
        private const double ROW_HEIGHT_LAP_TIMES = 50.0;         // Row 3: Last + Best lap
        private const double ROW_HEIGHT_RELATIVE = 250.0;         // Row 4: Relative table (7 cars)
        private const double ROW_HEIGHT_WARNINGS = 60.0;          // Row 5: Warnings
        private const double WINDOW_PADDING = 20.0;               // Top/bottom margins

        private readonly UserSettings _settings;
        private static SettingsManager _instance;

        /// <summary>
        /// Singleton instance of settings manager
        /// </summary>
        public static SettingsManager Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new SettingsManager();
                return _instance;
            }
        }

        // Events for real-time updates
        public event EventHandler<SettingsChangedEventArgs> SettingsChanged;
        public event EventHandler<WindowSizeChangedEventArgs> WindowSizeChanged;
        public event EventHandler<ElementVisibilityChangedEventArgs> ElementVisibilityChanged;
        public event EventHandler<RadarVisibilityChangedEventArgs> RadarVisibilityChanged;

        private SettingsManager()
        {
            _settings = UserSettings.Instance;
        }

        #region Window Dimensions

        /// <summary>
        /// Get main window dimensions based on current size preset and visible elements
        /// </summary>
        public Size GetMainWindowSize()
        {
            // Calculate base dimensions 
            double dynamicHeight = CalculateDynamicMainWindowHeight();
            double baseWidth = MAIN_WINDOW_WIDTH_LARGE;

            // Scale both width and height by the preset factor
            double scaleFactor = GetMainWindowScaleFactor(_settings.WindowSize);
            double scaledWidth = baseWidth * scaleFactor;
            double scaledHeight = dynamicHeight * scaleFactor;

            var result = new Size(scaledWidth, scaledHeight);
            System.Diagnostics.Debug.WriteLine($"[SettingsManager] GetMainWindowSize: preset={_settings.WindowSize}, baseSize={baseWidth}x{dynamicHeight}, scaleFactor={scaleFactor}, result={result.Width}x{result.Height}");

            return result;
        }

        /// <summary>
        /// Get radar window dimensions based on current size preset
        /// </summary>
        public Size GetRadarWindowSize()
        {
            return GetBaseDimensions(_settings.WindowSize, isMainWindow: false);
        }

        /// <summary>
        /// Calculate the dynamic height of main window based on visible elements
        /// </summary>
        private double CalculateDynamicMainWindowHeight()
        {
            double totalHeight = WINDOW_PADDING; // Start with base padding

            if (_settings.ShowRow0)
                totalHeight += ROW_HEIGHT_POSITION_GEAR;

            if (_settings.ShowRow1)
                totalHeight += ROW_HEIGHT_TIME_FUEL;

            if (_settings.ShowRow2)
                totalHeight += ROW_HEIGHT_DELTA_BAR;

            if (_settings.ShowRow3)
                totalHeight += ROW_HEIGHT_LAP_TIMES;

            if (_settings.ShowRow4)
                totalHeight += ROW_HEIGHT_RELATIVE;

            if (_settings.ShowRow5)
                totalHeight += ROW_HEIGHT_WARNINGS;

            totalHeight += WINDOW_PADDING; // Bottom padding

            // Ensure minimum height
            return Math.Max(totalHeight, 200.0);
        }

        /// <summary>
        /// Get base window dimensions for the specified size preset
        /// </summary>
        private Size GetBaseDimensions(WindowSizePreset sizePreset, bool isMainWindow)
        {
            if (isMainWindow)
            {
                return sizePreset switch
                {
                    WindowSizePreset.Small => new Size(MAIN_WINDOW_WIDTH_LARGE * 0.6, MAIN_WINDOW_HEIGHT_LARGE * 0.6),
                    WindowSizePreset.Medium => new Size(MAIN_WINDOW_WIDTH_LARGE * 0.8, MAIN_WINDOW_HEIGHT_LARGE * 0.8),
                    WindowSizePreset.Large => new Size(MAIN_WINDOW_WIDTH_LARGE, MAIN_WINDOW_HEIGHT_LARGE),
                    _ => new Size(MAIN_WINDOW_WIDTH_LARGE, MAIN_WINDOW_HEIGHT_LARGE)
                };
            }
            else // Radar window
            {
                return sizePreset switch
                {
                    WindowSizePreset.Small => new Size(RADAR_WINDOW_WIDTH_LARGE * 0.8, RADAR_WINDOW_HEIGHT_LARGE * 0.8),
                    WindowSizePreset.Medium => new Size(RADAR_WINDOW_WIDTH_LARGE * 0.9, RADAR_WINDOW_HEIGHT_LARGE * 0.9),
                    WindowSizePreset.Large => new Size(RADAR_WINDOW_WIDTH_LARGE, RADAR_WINDOW_HEIGHT_LARGE),
                    _ => new Size(RADAR_WINDOW_WIDTH_LARGE, RADAR_WINDOW_HEIGHT_LARGE)
                };
            }
        }

        /// <summary>
        /// Get the scale factor for main window based on size preset
        /// </summary>
        private double GetMainWindowScaleFactor(WindowSizePreset sizePreset)
        {
            return sizePreset switch
            {
                WindowSizePreset.Small => 0.6,
                WindowSizePreset.Medium => 0.8,
                WindowSizePreset.Large => 1.0,
                _ => 1.0
            };
        }

        #endregion

        #region Window Positioning

        /// <summary>
        /// Get main window position, using saved position or calculating default
        /// </summary>
        public Point GetMainWindowPosition()
        {
            if (_settings.MainWindowX >= 0 && _settings.MainWindowY >= 0)
            {
                return new Point(_settings.MainWindowX, _settings.MainWindowY);
            }

            // Calculate default position (current logic from MainWindow.xaml.cs)
            double centerX = SystemParameters.PrimaryScreenWidth / 2;
            double centerY = SystemParameters.PrimaryScreenHeight / 2;
            double offsetX = 900;
            double offsetY = 400;

            var windowSize = GetMainWindowSize();
            double left = centerX + offsetX - (windowSize.Width / 2);
            double top = centerY + offsetY - (windowSize.Height / 2);

            return new Point(left, top);
        }

        /// <summary>
        /// Get radar window position, using saved position or calculating default
        /// </summary>
        public Point GetRadarWindowPosition()
        {
            if (_settings.RadarWindowX >= 0 && _settings.RadarWindowY >= 0)
            {
                return new Point(_settings.RadarWindowX, _settings.RadarWindowY);
            }

            // Calculate default position (current logic from RadarWindow.xaml.cs)
            double centerX = SystemParameters.PrimaryScreenWidth / 2;
            double centerY = SystemParameters.PrimaryScreenHeight / 2;
            double offsetX = 400; // Right side
            double offsetY = -200; // Upper portion

            var windowSize = GetRadarWindowSize();
            double left = centerX + offsetX - (windowSize.Width / 2);
            double top = centerY + offsetY - (windowSize.Height / 2);

            // Ensure window stays on screen
            if (left + windowSize.Width > SystemParameters.PrimaryScreenWidth)
                left = SystemParameters.PrimaryScreenWidth - windowSize.Width - 20;
            if (top < 0)
                top = 20;

            return new Point(left, top);
        }

        /// <summary>
        /// Save current window positions
        /// </summary>
        public void SaveWindowPositions(Point mainWindowPos, Point radarWindowPos)
        {
            _settings.MainWindowX = (int)mainWindowPos.X;
            _settings.MainWindowY = (int)mainWindowPos.Y;
            _settings.RadarWindowX = (int)radarWindowPos.X;
            _settings.RadarWindowY = (int)radarWindowPos.Y;
            _settings.SaveSettings();

            System.Diagnostics.Debug.WriteLine($"[SettingsManager] Saved window positions - Main: ({mainWindowPos.X}, {mainWindowPos.Y}), Radar: ({radarWindowPos.X}, {radarWindowPos.Y})");
        }

        #endregion

        #region Element Visibility

        /// <summary>
        /// Check if radar window should be visible
        /// </summary>
        public bool IsRadarVisible => _settings.ShowRadar;

        /// <summary>
        /// Update element visibility settings with real-time notifications
        /// </summary>
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

            System.Diagnostics.Debug.WriteLine("[SettingsManager] Element visibility updated");

            // Fire events for real-time updates
            ElementVisibilityChanged?.Invoke(this, new ElementVisibilityChangedEventArgs
            {
                ShowPositionAndGear = showRow0,
                ShowTimeAndFuel = showRow1,
                ShowLapDelta = showRow2,
                ShowLapTimes = showRow3,
                ShowRelative = showRow4,
                ShowWarnings = showRow5
            });

            // Handle radar visibility changes
            if (oldRadarVisible != showRadar)
            {
                RadarVisibilityChanged?.Invoke(this, new RadarVisibilityChangedEventArgs { IsVisible = showRadar });
            }

            // Fire general settings changed event
            SettingsChanged?.Invoke(this, new SettingsChangedEventArgs { ChangeType = SettingsChangeType.ElementVisibility });
        }

        /// <summary>
        /// Update window size preset with real-time notifications
        /// </summary>
        public void UpdateWindowSize(WindowSizePreset newSize)
        {
            var oldSize = _settings.WindowSize;
            _settings.WindowSize = newSize;
            _settings.SaveSettings();

            System.Diagnostics.Debug.WriteLine($"[SettingsManager] Window size updated to {newSize}");

            // Fire events for real-time updates
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

        /// <summary>
        /// Direct access to UserSettings for advanced scenarios
        /// </summary>
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