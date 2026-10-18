using System.Configuration;
using System.Runtime.CompilerServices;
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
        private static UserSettings? _instance;

        public static UserSettings Instance => _instance ??= CreateWithRecovery();

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

        // Each setting is stored under its property name: renaming one resets it for everyone.
        private T Get<T>([CallerMemberName] string name = "") => (T)this[name];
        private void Set<T>(T value, [CallerMemberName] string name = "") => this[name] = value;

        #region Window Size and Position Settings

        [UserScopedSetting, DefaultSettingValue("Large")]
        public WindowSizePreset WindowSize { get => Get<WindowSizePreset>(); set => Set(value); }

        // Saved window positions; -1 means none saved yet, so the window takes its default place.
        [UserScopedSetting, DefaultSettingValue("-1")]
        public int MainWindowX { get => Get<int>(); set => Set(value); }

        [UserScopedSetting, DefaultSettingValue("-1")]
        public int MainWindowY { get => Get<int>(); set => Set(value); }

        [UserScopedSetting, DefaultSettingValue("-1")]
        public int RadarWindowX { get => Get<int>(); set => Set(value); }

        [UserScopedSetting, DefaultSettingValue("-1")]
        public int RadarWindowY { get => Get<int>(); set => Set(value); }

        #endregion

        #region Row Visibility Settings

        // Row 0: gear and position.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowRow0 { get => Get<bool>(); set => Set(value); }

        // Colour the Row 0 gear symbol as a shift indicator (amber approaching the shift point,
        // flashing red/white at it, solid red at the redline). Gated by ShowRow0.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowShiftIndicator { get => Get<bool>(); set => Set(value); }

        // Row 1: time and fuel.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowRow1 { get => Get<bool>(); set => Set(value); }

        // Row 2: delta bar.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowRow2 { get => Get<bool>(); set => Set(value); }

        // Row 3: lap times.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowRow3 { get => Get<bool>(); set => Set(value); }

        // Row 4: the relative display.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowRow4 { get => Get<bool>(); set => Set(value); }

        // Row 5: track location, incidents and track temperature, each with its own setting
        // below that only takes effect while Row 5 itself is shown.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowRow5 { get => Get<bool>(); set => Set(value); }

        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowTrackLocation { get => Get<bool>(); set => Set(value); }

        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowIncidentCounter { get => Get<bool>(); set => Set(value); }

        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowTrackTemp { get => Get<bool>(); set => Set(value); }

        [UserScopedSetting, DefaultSettingValue("true")]
        public bool ShowRadar { get => Get<bool>(); set => Set(value); }

        // Leaves pit-road cars out of the relative display; the player's own row always shows.
        [UserScopedSetting, DefaultSettingValue("false")]
        public bool HideCarsInPits { get => Get<bool>(); set => Set(value); }

        #endregion

        #region Position Display Settings

        // Class or overall position, in Row 0 and the relative display.
        [UserScopedSetting, DefaultSettingValue("Class")]
        public PositionDisplayMode PositionDisplayMode { get => Get<PositionDisplayMode>(); set => Set(value); }

        // Telemetry reports °C; Fahrenheit is converted for display.
        [UserScopedSetting, DefaultSettingValue("Fahrenheit")]
        public TemperatureUnit TemperatureUnit { get => Get<TemperatureUnit>(); set => Set(value); }

        #endregion

        #region Update and Debug Settings

        // Check GitHub for a newer release at startup.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool CheckForUpdatesOnStartup { get => Get<bool>(); set => Set(value); }

        // True until this version's settings have been carried over from the previous version's
        // (CarryOverFromPreviousVersion). Bookkeeping only; not in the Config window.
        [UserScopedSetting, DefaultSettingValue("true")]
        public bool UpgradeRequired { get => Get<bool>(); set => Set(value); }

        // Verbose logging.
        [UserScopedSetting, DefaultSettingValue("false")]
        public bool DebugModeEnabled { get => Get<bool>(); set => Set(value); }

        #endregion

        #region Helper Methods

        /// <summary>Saves the settings, logging rather than throwing on failure.</summary>
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