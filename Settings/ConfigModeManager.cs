using VISOR.Diagnostics;

namespace VISOR.Settings
{
    /// <summary>
    /// Config mode, on while the Config window is open: the windows show their drag handles and
    /// can be moved. Outside it they're click-through apart from specific controls.
    /// </summary>
    public class ConfigModeManager
    {
        public static ConfigModeManager Instance { get; } = new();

        /// <summary>Raised with the new state whenever config mode turns on or off.</summary>
        public event Action<bool>? ConfigModeChanged;

        public bool IsInConfigMode { get; private set; }

        private ConfigModeManager()
        {
            Log.Info("[ConfigModeManager] Initialized");
        }

        public void EnterConfigMode() => SetConfigMode(true);

        public void ExitConfigMode() => SetConfigMode(false);

        private void SetConfigMode(bool value)
        {
            if (IsInConfigMode == value)
                return;

            IsInConfigMode = value;
            Log.Debug($"[ConfigModeManager] Config mode changed to: {value}");
            ConfigModeChanged?.Invoke(value);
        }
    }
}
