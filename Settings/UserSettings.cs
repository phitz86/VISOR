using System;
using System.ComponentModel;
using System.Configuration;
using System.IO;
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
        private static UserSettings _instance = null!;

        /// <summary>
        /// Singleton instance of user settings
        /// </summary>
        public static UserSettings Instance
        {
            get
            {
                if (_instance == null)
                    _instance = CreateWithRecovery();
                return _instance;
            }
        }

        /// <summary>
        /// Creates the settings instance, recovering automatically if the underlying
        /// user.config file is corrupt. A corrupt config otherwise throws
        /// ConfigurationErrorsException on first property access, which would crash
        /// VISOR at startup on every launch until the file is manually deleted.
        /// </summary>
        private static UserSettings CreateWithRecovery()
        {
            var settings = new UserSettings();
            try
            {
                // Force the user.config to load by touching a persisted value.
                _ = settings.WindowSize;
                return settings;
            }
            catch (ConfigurationErrorsException ex)
            {
                Log.Error("User settings file is corrupt; backing up and resetting to defaults", ex);
                BackupCorruptConfig(ex);

                // Re-create against the now-removed file so defaults load cleanly.
                settings = new UserSettings();
                try
                {
                    settings.Reset();
                    settings.Save();
                }
                catch (Exception inner)
                {
                    Log.Error("Failed to reset settings after corruption recovery", inner);
                }
                return settings;
            }
        }

        /// <summary>
        /// Moves a corrupt user.config aside (renamed with a timestamp) so the next
        /// load starts fresh while preserving the bad file for diagnostics.
        /// </summary>
        private static void BackupCorruptConfig(ConfigurationErrorsException ex)
        {
            // The offending path is sometimes on the inner exception rather than the outer.
            string? path = ex.Filename;
            if (string.IsNullOrEmpty(path) && ex.InnerException is ConfigurationErrorsException inner)
                path = inner.Filename;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Log.Warning("Could not determine corrupt settings file path; skipping backup");
                return;
            }

            try
            {
                string backupPath = $"{path}.corrupt-{DateTime.Now:yyyyMMdd_HHmmss}.bak";
                File.Copy(path, backupPath, overwrite: true);
                File.Delete(path);
                Log.Info($"Backed up corrupt settings to {backupPath}");
            }
            catch (Exception bex)
            {
                Log.Error("Failed to back up corrupt settings file", bex);
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

        /// <summary>
        /// Hide cars that are on pit road from the Row 4 relative display.
        /// Affects only the relative display; the player's own row is always shown.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("false")]
        public bool HideCarsInPits
        {
            get => (bool)this["HideCarsInPits"];
            set => this["HideCarsInPits"] = value;
        }

        #endregion

        #region Position Display Settings

        /// <summary>
        /// Whether position displays (Row 0 player position and the relative table)
        /// show class position or overall (field-wide) position.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("Class")]
        public PositionDisplayMode PositionDisplayMode
        {
            get => (PositionDisplayMode)this["PositionDisplayMode"];
            set => this["PositionDisplayMode"] = value;
        }

        /// <summary>
        /// Display unit for the Row 5 track temperature element.
        /// Telemetry reports °C; Fahrenheit is a display-time conversion.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("Fahrenheit")]
        public TemperatureUnit TemperatureUnit
        {
            get => (TemperatureUnit)this["TemperatureUnit"];
            set => this["TemperatureUnit"] = value;
        }

        #endregion

        #region Update Settings

        /// <summary>
        /// Whether VISOR checks GitHub for a newer release on startup and notifies
        /// the user when one is available.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool CheckForUpdatesOnStartup
        {
            get => (bool)this["CheckForUpdatesOnStartup"];
            set => this["CheckForUpdatesOnStartup"] = value;
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

    /// <summary>
    /// How race positions are displayed throughout the overlay.
    /// Class = position within the car's own class (default).
    /// Overall = position across the entire field, regardless of class.
    /// </summary>
    public enum PositionDisplayMode
    {
        Class,
        Overall
    }

    /// <summary>
    /// Unit used to display temperatures (track temp in Row 5).
    /// </summary>
    public enum TemperatureUnit
    {
        Celsius,
        Fahrenheit
    }
}