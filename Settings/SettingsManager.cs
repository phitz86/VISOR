using System.Windows;
using VISOR.Diagnostics;

namespace VISOR.Settings
{
    public class SettingsManager
    {
        private const double MAIN_WINDOW_WIDTH_LARGE = 640.0;
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
        // constant: folded into the scaled padding instead, it shrinks to ~14px at the 0.6x
        // Small preset and clips the bottom of Row 5.
        private const double BORDER_CHROME_HEIGHT = 20.0;

        private readonly UserSettings _settings;
        private static SettingsManager? _instance;

        public static SettingsManager Instance => _instance ??= new SettingsManager();

        public event EventHandler<WindowSizeChangedEventArgs>? WindowSizeChanged;
        public event EventHandler? ElementVisibilityChanged;

        private SettingsManager()
        {
            _settings = UserSettings.Instance;
        }

        #region Window Dimensions

        /// <param name="relativeHidden">The session hides the relative (lone qualifying), so its row takes no space.</param>
        public Size GetMainWindowSize(bool relativeHidden = false)
        {
            double contentHeight = CalculateDynamicMainWindowHeight(relativeHidden);
            double baseWidth = MAIN_WINDOW_WIDTH_LARGE;

            double scaleFactor = WindowScale.ForMainWindow(_settings.WindowSize);
            double scaledWidth = baseWidth * scaleFactor;

            // Scale the content, then add the unscaled Border chrome.
            double scaledHeight = contentHeight * scaleFactor + BORDER_CHROME_HEIGHT;

            return new Size(scaledWidth, scaledHeight);
        }

        public Size GetRadarWindowSize()
        {
            double scale = WindowScale.ForRadar(_settings.WindowSize);
            return new Size(RADAR_WINDOW_WIDTH_LARGE * scale, RADAR_WINDOW_HEIGHT_LARGE * scale);
        }

        /// <summary>
        /// Sum of the visible rows plus one WINDOW_PADDING of interior breathing room,
        /// unscaled. This is the content the scaled grid needs; the window's fixed Border
        /// chrome is added separately (and unscaled) in <see cref="GetMainWindowSize"/>.
        /// </summary>
        private double CalculateDynamicMainWindowHeight(bool relativeHidden)
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

            if (_settings.ShowRow4 && !relativeHidden)
                totalHeight += ROW_HEIGHT_RELATIVE;

            if (_settings.ShowRow5)
                totalHeight += ROW_HEIGHT_WARNINGS;

            totalHeight += WINDOW_PADDING;

            return Math.Max(totalHeight, 200.0);
        }

        #endregion

        #region Window Positioning

        public Point GetMainWindowPosition()
        {
            var size = GetMainWindowSize();
            return SavedPosition(_settings.MainWindowX, _settings.MainWindowY, size)
                ?? DefaultPosition(size, offsetX: 900, offsetY: 400);
        }

        public Point GetRadarWindowPosition()
        {
            var size = GetRadarWindowSize();
            if (SavedPosition(_settings.RadarWindowX, _settings.RadarWindowY, size) is Point saved)
                return saved;

            var position = DefaultPosition(size, offsetX: 400, offsetY: -200);
            if (position.X + size.Width > SystemParameters.PrimaryScreenWidth)
                position.X = SystemParameters.PrimaryScreenWidth - size.Width - 20;
            if (position.Y < 0)
                position.Y = 20;

            return position;
        }

        // The saved position pulled back on-screen, or null when the window hasn't been placed yet.
        private static Point? SavedPosition(int x, int y, Size windowSize) =>
            x >= 0 && y >= 0 ? ClampToVisibleArea(new Point(x, y), windowSize) : null;

        // Where a window goes until it is first moved: offset from the primary screen's centre.
        private static Point DefaultPosition(Size windowSize, double offsetX, double offsetY) =>
            new(SystemParameters.PrimaryScreenWidth / 2 + offsetX - windowSize.Width / 2,
                SystemParameters.PrimaryScreenHeight / 2 + offsetY - windowSize.Height / 2);

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
            _settings.ShowRow0 = showRow0;
            _settings.ShowRow1 = showRow1;
            _settings.ShowRow2 = showRow2;
            _settings.ShowRow3 = showRow3;
            _settings.ShowRow4 = showRow4;
            _settings.ShowRow5 = showRow5;
            _settings.ShowRadar = showRadar;
            _settings.SaveSettings();

            ElementVisibilityChanged?.Invoke(this, EventArgs.Empty);
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

            ElementVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdatePositionDisplayMode(PositionDisplayMode newMode)
        {
            _settings.PositionDisplayMode = newMode;
            _settings.SaveSettings();
        }

        public void UpdateWindowSize(WindowSizePreset newSize)
        {
            var oldSize = _settings.WindowSize;
            _settings.WindowSize = newSize;
            _settings.SaveSettings();

            if (oldSize != newSize)
            {
                WindowSizeChanged?.Invoke(this, new WindowSizeChangedEventArgs { NewSize = newSize });
            }
        }

        #endregion

        #region Settings Access

        public UserSettings Settings => _settings;

        #endregion
    }

    #region Event Args

    public class WindowSizeChangedEventArgs : EventArgs
    {
        public WindowSizePreset NewSize { get; set; }
    }

    #endregion
}