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
        private readonly RadarWindow _radarWindow;
        private readonly SettingsManager _settingsManager;

        // Events for communication with App.xaml.cs
        public event EventHandler ExitRequested;

        public ConfigWindow(SVappsLABSDKWrapper telemetry, MainWindow mainWindow, RadarWindow radarWindow)
        {
            InitializeComponent();

            _telemetry = telemetry;
            _mainWindow = mainWindow;
            _radarWindow = radarWindow;
            _settingsManager = new SettingsManager();

            // Initialize UI with current settings
            LoadCurrentSettings();
        }

        private void LoadCurrentSettings()
        {
            var settings = _settingsManager.Settings;

            // Set window size combo
            WindowSizeCombo.SelectedIndex = settings.WindowSize switch
            {
                WindowSizePreset.Small => 0,
                WindowSizePreset.Medium => 1,
                WindowSizePreset.Large => 2,
                _ => 2 // Default to Large
            };

            // Set row visibility checkboxes
            Row0CheckBox.IsChecked = settings.ShowRow0;
            Row1CheckBox.IsChecked = settings.ShowRow1;
            Row2CheckBox.IsChecked = settings.ShowRow2;
            Row3CheckBox.IsChecked = settings.ShowRow3;
            Row4CheckBox.IsChecked = settings.ShowRow4;
            Row5CheckBox.IsChecked = settings.ShowRow5;

            // Set radar checkbox
            RadarCheckBox.IsChecked = settings.ShowRadar;

            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Loaded current settings into UI");
        }

        private void WindowSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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
            // Get current checkbox states
            bool showRow0 = Row0CheckBox.IsChecked ?? false;
            bool showRow1 = Row1CheckBox.IsChecked ?? false;
            bool showRow2 = Row2CheckBox.IsChecked ?? false;
            bool showRow3 = Row3CheckBox.IsChecked ?? false;
            bool showRow4 = Row4CheckBox.IsChecked ?? false;
            bool showRow5 = Row5CheckBox.IsChecked ?? false;
            bool showRadar = RadarCheckBox.IsChecked ?? false;

            System.Diagnostics.Debug.WriteLine($"[ConfigWindow] Row visibility changed - updating settings");

            // Update settings (this will trigger real-time updates via events)
            _settingsManager.UpdateElementVisibility(
                showRow0, showRow1, showRow2, showRow3, showRow4, showRow5, showRadar);
        }

        private void RadarCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool showRadar = RadarCheckBox.IsChecked ?? false;

            System.Diagnostics.Debug.WriteLine($"[ConfigWindow] Radar visibility changed to {showRadar}");

            // Update radar visibility immediately
            if (showRadar)
            {
                if (_radarWindow != null)
                {
                    _radarWindow.Show();
                    _radarWindow.Activate();
                }
                else
                {
                    // Request App.xaml.cs to create radar window
                    ((App)Application.Current).ShowRadarWindow();
                }
            }
            else
            {
                if (_radarWindow != null)
                {
                    _radarWindow.Hide();
                }
                else
                {
                    ((App)Application.Current).HideRadarWindow();
                }
            }

            // Also update the settings to include all current states
            RowCheckBox_Changed(sender, e);
        }

        private void DoneButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Done button clicked - closing config window");

            // All settings have been applied in real-time, so just close the window
            this.Close();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Exit button clicked - requesting application shutdown");

            // Request the application to shutdown
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClosed(EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ConfigWindow] Config window closed");
            base.OnClosed(e);
        }
    }
}