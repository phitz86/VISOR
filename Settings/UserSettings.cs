using System.ComponentModel;
using System.Configuration;
using VISOR.Diagnostics;

namespace VISOR.Settings
{
    /// <summary>
    /// User configuration settings for VISOR application.
    /// Uses .NET Application Settings for automatic persistence and validation.
    /// Focuses on row visibility control with fixed element layouts.
    /// </summary>
    public sealed class UserSettings : ApplicationSettingsBase
    {
        private static UserSettings _instance;

        /// <summary>
        /// Singleton instance of user settings
        /// </summary>
        public static UserSettings Instance
        {
            get
            {
                if (_instance == null)
                    _instance = new UserSettings();
                return _instance;
            }
        }

        #region Window Size and Position Settings

        /// <summary>
        /// Size preset for application windows (Small/Medium/Large)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("Large")]
        public WindowSizePreset WindowSize
        {
            get => (WindowSizePreset)this["WindowSize"];
            set => this["WindowSize"] = value;
        }

        /// <summary>
        /// Main window X position (-1 = use default)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("-1")]
        public int MainWindowX
        {
            get => (int)this["MainWindowX"];
            set => this["MainWindowX"] = value;
        }

        /// <summary>
        /// Main window Y position (-1 = use default)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("-1")]
        public int MainWindowY
        {
            get => (int)this["MainWindowY"];
            set => this["MainWindowY"] = value;
        }

        /// <summary>
        /// Radar window X position (-1 = use default)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("-1")]
        public int RadarWindowX
        {
            get => (int)this["RadarWindowX"];
            set => this["RadarWindowX"] = value;
        }

        /// <summary>
        /// Radar window Y position (-1 = use default)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("-1")]
        public int RadarWindowY
        {
            get => (int)this["RadarWindowY"];
            set => this["RadarWindowY"] = value;
        }

        #endregion

        #region Row Visibility Settings

        /// <summary>
        /// Show Row 0 (Gear + Position)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowRow0
        {
            get => (bool)this["ShowRow0"];
            set => this["ShowRow0"] = value;
        }

        /// <summary>
        /// Show Row 1 (Time + Fuel)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowRow1
        {
            get => (bool)this["ShowRow1"];
            set => this["ShowRow1"] = value;
        }

        /// <summary>
        /// Show Row 2 (Delta Bar)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowRow2
        {
            get => (bool)this["ShowRow2"];
            set => this["ShowRow2"] = value;
        }

        /// <summary>
        /// Show Row 3 (Lap Times)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowRow3
        {
            get => (bool)this["ShowRow3"];
            set => this["ShowRow3"] = value;
        }

        /// <summary>
        /// Show Row 4 (Relative Display)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowRow4
        {
            get => (bool)this["ShowRow4"];
            set => this["ShowRow4"] = value;
        }

        /// <summary>
        /// Show Row 5 (Warnings)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowRow5
        {
            get => (bool)this["ShowRow5"];
            set => this["ShowRow5"] = value;
        }

        /// <summary>
        /// Show radar window
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowRadar
        {
            get => (bool)this["ShowRadar"];
            set => this["ShowRadar"] = value;
        }

        #endregion

        #region Debug Settings

        /// <summary>
        /// Enable debug mode (verbose logging)
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("false")]
        public bool DebugModeEnabled
        {
            get => (bool)this["DebugModeEnabled"];
            set => this["DebugModeEnabled"] = value;
        }

        /// <summary>
        /// Use SDK's parsed TelemetrySessionInfo instead of VISOR's hand-rolled YAML parsers.
        /// When false, both parsers run side-by-side and divergences log as [ParserDiff].
        /// When true, only the SDK adapter runs.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool UseSdkParser
        {
            get => (bool)this["UseSdkParser"];
            set => this["UseSdkParser"] = value;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Save current settings to storage
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                this.Save();
                Log.Debug("Settings saved successfully");
            }
            catch (System.Exception ex)
            {
                Log.Error("Error saving settings", ex);
            }
        }

        /// <summary>
        /// Reload settings from storage
        /// </summary>
        public void ReloadSettings()
        {
            try
            {
                this.Reload();
                Log.Debug("Settings reloaded successfully");
            }
            catch (System.Exception ex)
            {
                Log.Error("Error reloading settings", ex);
            }
        }

        /// <summary>
        /// Reset all settings to default values
        /// </summary>
        public void ResetToDefaults()
        {
            try
            {
                this.Reset();
                Log.Info("Settings reset to defaults");
            }
            catch (System.Exception ex)
            {
                Log.Error("Error resetting settings", ex);
            }
        }

        /// <summary>
        /// Get row visibility setting by row index
        /// </summary>
        public bool GetRowVisibility(int rowIndex)
        {
            return rowIndex switch
            {
                0 => ShowRow0,
                1 => ShowRow1,
                2 => ShowRow2,
                3 => ShowRow3,
                4 => ShowRow4,
                5 => ShowRow5,
                _ => false
            };
        }

        /// <summary>
        /// Set row visibility setting by row index
        /// </summary>
        public void SetRowVisibility(int rowIndex, bool visible)
        {
            switch (rowIndex)
            {
                case 0: ShowRow0 = visible; break;
                case 1: ShowRow1 = visible; break;
                case 2: ShowRow2 = visible; break;
                case 3: ShowRow3 = visible; break;
                case 4: ShowRow4 = visible; break;
                case 5: ShowRow5 = visible; break;
            }
        }

        #endregion
    }

    /// <summary>
    /// Available window size presets
    /// </summary>
    public enum WindowSizePreset
    {
        Small,
        Medium,
        Large
    }
}