using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using VISOR.Diagnostics;
using VISOR.Telemetry;
using VISOR.Settings;
using VISOR.Update;

namespace VISOR.Views
{
    public partial class ConfigWindow : Window
    {
        private readonly SVappsLABSDKWrapper _telemetry;
        private readonly MainWindow _mainWindow;
        private readonly SettingsManager _settingsManager;
        private readonly ConfigModeManager _configModeManager;
        private bool _isInitialized = false;

        public event EventHandler? ExitRequested;

        public ConfigWindow(SVappsLABSDKWrapper telemetry, MainWindow mainWindow)
        {
            InitializeComponent();

            _telemetry = telemetry;
            _mainWindow = mainWindow;
            _settingsManager = SettingsManager.Instance;
            _configModeManager = ConfigModeManager.Instance;

            // Set version display from assembly
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null
                ? $"Version {version.Major}.{version.Minor}.{version.Build}"
                : "Version unknown";

            _configModeManager.EnterConfigMode();

            CenterOnPrimaryScreen();

            var radarWindow = GetCurrentRadarWindow();
            if (radarWindow != null)
            {
                radarWindow.SetForceVisible(true);
            }

            LoadCurrentSettings();
            UpdateDebugModeDisplay();
            _isInitialized = true;

            // Surface an update if one was already found, and listen for one that
            // arrives while this window is open. Storing the result statically means
            // a Config window reopened after the check still shows the notification.
            if (UpdateChecker.AvailableUpdate != null)
                ShowUpdateAvailable(UpdateChecker.AvailableUpdate);
            UpdateChecker.UpdateAvailable += OnUpdateAvailable;

            Log.Info("ConfigWindow opened - config mode enabled");
        }

        private RadarWindow? GetCurrentRadarWindow()
        {
            return ((App)Application.Current).CurrentRadarWindow;
        }

        /// <summary>
        /// Anchors the Config window to the center of the primary screen ("Screen 1")
        /// on startup. WPF's CenterScreen centers on whichever monitor holds the mouse
        /// cursor, which is unpredictable on multi-monitor sim rigs; this is
        /// deterministic. The primary screen's work area always sits at the origin of
        /// the virtual desktop, so this keeps the window on Screen 1 and clear of the
        /// taskbar.
        /// </summary>
        private void CenterOnPrimaryScreen()
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }

        private void LoadCurrentSettings()
        {
            var settings = _settingsManager.Settings;

            WindowSizeCombo.SelectedIndex = settings.WindowSize switch
            {
                WindowSizePreset.Small => 0,
                WindowSizePreset.Medium => 1,
                WindowSizePreset.Large => 2,
                _ => 2
            };

            Row0CheckBox.IsChecked = settings.ShowRow0;
            Row1CheckBox.IsChecked = settings.ShowRow1;
            Row2CheckBox.IsChecked = settings.ShowRow2;
            Row3CheckBox.IsChecked = settings.ShowRow3;
            Row4CheckBox.IsChecked = settings.ShowRow4;
            HidePitsCheckBox.IsChecked = settings.HideCarsInPits;
            Row5CheckBox.IsChecked = settings.ShowRow5;
            TrackLocationCheckBox.IsChecked = settings.ShowTrackLocation;
            IncidentCheckBox.IsChecked = settings.ShowIncidentCounter;
            TrackTempCheckBox.IsChecked = settings.ShowTrackTemp;
            RadarCheckBox.IsChecked = settings.ShowRadar;
            DebugModeCheckBox.IsChecked = settings.DebugModeEnabled;

            if (settings.PositionDisplayMode == PositionDisplayMode.Overall)
                PositionOverallRadio.IsChecked = true;
            else
                PositionClassRadio.IsChecked = true;

            if (settings.TemperatureUnit == TemperatureUnit.Celsius)
                TempCelsiusRadio.IsChecked = true;
            else
                TempFahrenheitRadio.IsChecked = true;

            Log.Debug("Loaded current settings into UI");
        }

        private void UpdateDebugModeDisplay()
        {
            string logPath = Log.GetCurrentLogPath();
            if (!string.IsNullOrEmpty(logPath))
            {
                string fileName = Path.GetFileName(logPath);
                CurrentLogFileText.Text = $"Current log: {fileName}";
            }
            else
            {
                CurrentLogFileText.Text = "No log file active";
            }
        }

        private void WindowSizeCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            if (WindowSizeCombo.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string sizeTag)
            {
                var newSize = sizeTag switch
                {
                    "Small" => WindowSizePreset.Small,
                    "Medium" => WindowSizePreset.Medium,
                    "Large" => WindowSizePreset.Large,
                    _ => WindowSizePreset.Large
                };

                Log.Debug($"Window size changed to {newSize}");
                _settingsManager.UpdateWindowSize(newSize);
            }
        }

        private void DebugModeCheckBox_Checked(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
                return;

            var settings = _settingsManager.Settings;
            settings.DebugModeEnabled = true;
            settings.SaveSettings();
            Log.DebugModeEnabled = true;
            Log.Info("Debug mode enabled - verbose logging activated");
            UpdateDebugModeDisplay();
        }

        private void DebugModeCheckBox_Unchecked(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
                return;

            var settings = _settingsManager.Settings;
            settings.DebugModeEnabled = false;
            settings.SaveSettings();
            Log.Info("Debug mode disabled - returning to normal logging");
            Log.DebugModeEnabled = false;
            UpdateDebugModeDisplay();
        }

        private void OpenLogsFolderButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                string logsDirectory = Log.GetLogsDirectory();

                if (!Directory.Exists(logsDirectory))
                {
                    Directory.CreateDirectory(logsDirectory);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{logsDirectory}\"",
                    UseShellExecute = false
                });
                Log.Info("Opened logs folder in Explorer");
            }
            catch (Exception ex)
            {
                Log.Error("Failed to open logs folder", ex);
                MessageBox.Show($"Could not open logs folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RowCheckBox_Changed(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            bool showRow0 = Row0CheckBox.IsChecked ?? false;
            bool showRow1 = Row1CheckBox.IsChecked ?? false;
            bool showRow2 = Row2CheckBox.IsChecked ?? false;
            bool showRow3 = Row3CheckBox.IsChecked ?? false;
            bool showRow4 = Row4CheckBox.IsChecked ?? false;
            bool showRow5 = Row5CheckBox.IsChecked ?? false;
            bool showRadar = RadarCheckBox.IsChecked ?? false;

            Log.Debug("Row visibility changed - updating settings");

            _settingsManager.UpdateElementVisibility(
                showRow0, showRow1, showRow2, showRow3, showRow4, showRow5, showRadar);
        }

        private void Row5ElementCheckBox_Changed(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            bool showTrackLocation = TrackLocationCheckBox.IsChecked ?? false;
            bool showIncident = IncidentCheckBox.IsChecked ?? false;
            bool showTrackTemp = TrackTempCheckBox.IsChecked ?? false;

            Log.Info($"Row 5 elements changed - location:{showTrackLocation} incident:{showIncident} temp:{showTrackTemp}");
            _settingsManager.UpdateRow5ElementVisibility(showTrackLocation, showIncident, showTrackTemp);
        }

        private void PositionDisplayRadio_Changed(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            var newMode = PositionOverallRadio.IsChecked == true
                ? PositionDisplayMode.Overall
                : PositionDisplayMode.Class;

            Log.Info($"Position display mode changed to {newMode}");
            _settingsManager.UpdatePositionDisplayMode(newMode);
        }

        private void TempUnitRadio_Changed(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            var settings = _settingsManager.Settings;
            settings.TemperatureUnit = TempCelsiusRadio.IsChecked == true
                ? TemperatureUnit.Celsius
                : TemperatureUnit.Fahrenheit;
            settings.SaveSettings();

            Log.Info($"Temperature unit changed to {settings.TemperatureUnit}");
        }

        private void HidePitsCheckBox_Changed(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            var settings = _settingsManager.Settings;
            settings.HideCarsInPits = HidePitsCheckBox.IsChecked ?? false;
            settings.SaveSettings();

            Log.Info($"Hide cars in pits changed to {settings.HideCarsInPits}");
        }

        private void RadarCheckBox_Changed(object? sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            bool showRadar = RadarCheckBox.IsChecked ?? false;

            Log.Info($"Radar visibility changed to {showRadar}");

            if (showRadar)
            {
                var radarWindow = GetCurrentRadarWindow();
                if (radarWindow != null)
                {
                    radarWindow.Show();
                    radarWindow.Activate();
                }
                else
                {
                    ((App)Application.Current).ShowRadarWindow();
                }
            }
            else
            {
                ((App)Application.Current).HideRadarWindow();
            }

            RowCheckBox_Changed(sender, e);
        }

        private void DoneButton_Click(object? sender, RoutedEventArgs e)
        {
            Log.Debug("Done button clicked - closing config window");
            Close();
        }

        private void ExitButton_Click(object? sender, RoutedEventArgs e)
        {
            Log.Debug("Exit button clicked - requesting application shutdown");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        private void OnUpdateAvailable(Version latestVersion)
        {
            // The check may complete on a background continuation; marshal to the UI.
            Dispatcher.Invoke(() => ShowUpdateAvailable(latestVersion));
        }

        /// <summary>
        /// Reveals the green "Version x.y.z available" pill next to the version label.
        /// </summary>
        private void ShowUpdateAvailable(Version latestVersion)
        {
            UpdateButton.Content = $"Version {latestVersion.Major}.{latestVersion.Minor}.{latestVersion.Build} available";
            UpdateButton.Visibility = Visibility.Visible;
        }

        private void UpdateButton_Click(object? sender, RoutedEventArgs e)
        {
            Log.Info("Update notification clicked - opening releases page");
            UpdateChecker.OpenReleasesPage();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            Log.Info("ConfigWindow closing - saving window positions and exiting config mode");

            // Static event would otherwise keep this closed window alive.
            UpdateChecker.UpdateAvailable -= OnUpdateAvailable;

            SaveWindowPositions();
            _configModeManager.ExitConfigMode();

            var radarWindow = GetCurrentRadarWindow();
            if (radarWindow != null)
            {
                radarWindow.SetForceVisible(false);
            }

            base.OnClosing(e);
        }

        private void SaveWindowPositions()
        {
            if (_mainWindow != null)
            {
                Point mainPos = new Point(_mainWindow.Left, _mainWindow.Top);

                var radarWindow = GetCurrentRadarWindow();
                Point radarPos = radarWindow != null
                    ? new Point(radarWindow.Left, radarWindow.Top)
                    : _settingsManager.GetRadarWindowPosition();

                _settingsManager.SaveWindowPositions(mainPos, radarPos);

                Log.Debug($"Positions saved - Main: ({mainPos.X}, {mainPos.Y}), Radar: ({radarPos.X}, {radarPos.Y})");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }
    }
}