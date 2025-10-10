using System;
using System.Windows;
using System.Windows.Controls;
using VISOR.Telemetry;
using VISOR.Settings;

namespace VISOR.Views
{
    public partial class ConfigWindow : Window
    {
        private readonly SVappsLABSDKWrapper _telemetry;
        private readonly MainWindow _mainWindow;
        private readonly SettingsManager _settingsManager;
        private readonly ConfigModeManager _configModeManager;
        private bool _isInitialized = false;

        public event EventHandler ExitRequested;

        public ConfigWindow(SVappsLABSDKWrapper telemetry, MainWindow mainWindow)
        {
            InitializeComponent();

            _telemetry = telemetry;
            _mainWindow = mainWindow;
            _settingsManager = SettingsManager.Instance;
            _configModeManager = ConfigModeManager.Instance;

            // Set version display from assembly
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = $"Version {version.Major}.{version.Minor}.{version.Build}";

            _configModeManager.EnterConfigMode();

            var radarWindow = GetCurrentRadarWindow();
            if (radarWindow != null)
            {
                radarWindow.SetForceVisible(true);
            }

            LoadCurrentSettings();
            _isInitialized = true;

            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Opened - config mode enabled");
        }

        private RadarWindow GetCurrentRadarWindow()
        {
            return ((App)Application.Current).CurrentRadarWindow;
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
            Row5CheckBox.IsChecked = settings.ShowRow5;
            RadarCheckBox.IsChecked = settings.ShowRadar;

            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Loaded current settings into UI");
        }

        private void WindowSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

                System.Diagnostics.Debug.WriteLine($"[ConfigWindow] Window size changed to {newSize}");
                _settingsManager.UpdateWindowSize(newSize);
            }
        }

        private void RowCheckBox_Changed(object sender, RoutedEventArgs e)
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

            System.Diagnostics.Debug.WriteLine($"[ConfigWindow] Row visibility changed - updating settings");

            _settingsManager.UpdateElementVisibility(
                showRow0, showRow1, showRow2, showRow3, showRow4, showRow5, showRadar);
        }

        private void RadarCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized || _settingsManager == null)
                return;

            bool showRadar = RadarCheckBox.IsChecked ?? false;

            System.Diagnostics.Debug.WriteLine($"[ConfigWindow] Radar visibility changed to {showRadar}");

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

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Done button clicked - closing config window");
            Close();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Exit button clicked - requesting application shutdown");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Closing - saving window positions and exiting config mode");

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

                System.Diagnostics.Debug.WriteLine($"[ConfigWindow] Positions saved - Main: ({mainPos.X}, {mainPos.Y}), Radar: ({radarPos.X}, {radarPos.Y})");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Config window closed");
            base.OnClosed(e);
        }
    }
}