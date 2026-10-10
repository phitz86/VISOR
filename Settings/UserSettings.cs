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
                CarryOverFromPreviousVersion(settings);
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
        /// Settings are stored per app version (in a folder named after the version), so a new
        /// version starts from defaults unless the previous version's values are copied forward.
        /// UpgradeRequired is true in a version's fresh settings, so this does its work once per
        /// version: on the first start after an update, or after a fresh install, where there is
        /// nothing to copy.
        /// </summary>
        private static void CarryOverFromPreviousVersion(UserSettings settings)
        {
            if (!settings.UpgradeRequired)
                return;

            try
            {
                // Only a version with no settings file of its own is new. A build that adds this
                // check without changing the version already has one, and an older version's
                // values must not overwrite it. GetPreviousVersion is null when no earlier
                // version's settings exist.
                string currentFile = ConfigurationManager
                    .OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
                if (!File.Exists(currentFile) && settings.GetPreviousVersion(nameof(WindowSize)) != null)
                {
                    settings.Upgrade();
                    Log.Info("Settings carried over from the previous version");
                }
            }
            catch (Exception ex)
            {
                // A corrupt previous-version file must not reach the corrupt-config recovery in
                // CreateWithRecovery, which would delete it. Keep this version's settings instead.
                Log.Warning($"Could not carry settings over from the previous version ({ex.GetType().Name}: {ex.Message}); keeping this version's settings");
            }

            try
            {
                settings.UpgradeRequired = false;
                settings.Save();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save settings after carrying them over", ex);
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
        /// Color the Row 0 gear symbol as a shift indicator (amber approaching the shift point,
        /// flashing red/white at it, solid red at the redline). Gated by ShowRow0.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowShiftIndicator
        {
            get => (bool)this["ShowShiftIndicator"];
            set => this["ShowShiftIndicator"] = value;
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
        /// Show the Row 5 track-location readout (named corner/section of the circuit).
        /// Gated by ShowRow5; only takes effect while Row 5 itself is visible.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowTrackLocation
        {
            get => (bool)this["ShowTrackLocation"];
            set => this["ShowTrackLocation"] = value;
        }

        /// <summary>
        /// Show the Row 5 incident counter. Gated by ShowRow5.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowIncidentCounter
        {
            get => (bool)this["ShowIncidentCounter"];
            set => this["ShowIncidentCounter"] = value;
        }

        /// <summary>
        /// Show the Row 5 track-temperature readout. Gated by ShowRow5.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool ShowTrackTemp
        {
            get => (bool)this["ShowTrackTemp"];
            set => this["ShowTrackTemp"] = value;
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

        #region Version Upgrade

        /// <summary>
        /// True until this version's settings have been carried over from the previous version's
        /// (see CarryOverFromPreviousVersion). Internal bookkeeping; not shown in the Config window.
        /// </summary>
        [UserScopedSetting]
        [DefaultSettingValue("true")]
        public bool UpgradeRequired
        {
            get => (bool)this["UpgradeRequired"];
            set => this["UpgradeRequired"] = value;
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
    /// How much each size preset scales the windows. The overlay and the radar scale differently,
    /// so the radar stays readable at Small.
    /// </summary>
    public static class WindowScale
    {
        public static double ForMainWindow(WindowSizePreset preset) => preset switch
        {
            WindowSizePreset.Small => 0.6,
            WindowSizePreset.Medium => 0.8,
            _ => 1.0
        };

        public static double ForRadar(WindowSizePreset preset) => preset switch
        {
            WindowSizePreset.Small => 0.8,
            WindowSizePreset.Medium => 0.9,
            _ => 1.0
        };
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