using System;

namespace VISOR.Settings
{
    /// <summary>
    /// Manages the application's configuration mode state.
    /// When in config mode, windows can be dragged via their drag handles.
    /// When not in config mode, windows are click-through except for specific controls.
    /// </summary>
    public class ConfigModeManager
    {
        private static ConfigModeManager _instance;
        private static readonly object _lock = new object();

        private bool _isInConfigMode;

        /// <summary>
        /// Event raised when config mode state changes
        /// </summary>
        public event EventHandler<ConfigModeChangedEventArgs> ConfigModeChanged;

        /// <summary>
        /// Gets whether the application is currently in configuration mode
        /// </summary>
        public bool IsInConfigMode
        {
            get => _isInConfigMode;
            private set
            {
                if (_isInConfigMode != value)
                {
                    _isInConfigMode = value;
                    System.Diagnostics.Debug.WriteLine($"[ConfigModeManager] Config mode changed to: {value}");
                    ConfigModeChanged?.Invoke(this, new ConfigModeChangedEventArgs(value));
                }
            }
        }

        /// <summary>
        /// Gets the singleton instance of ConfigModeManager
        /// </summary>
        public static ConfigModeManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new ConfigModeManager();
                        }
                    }
                }
                return _instance;
            }
        }

        private ConfigModeManager()
        {
            _isInConfigMode = false;
            System.Diagnostics.Debug.WriteLine("[ConfigModeManager] Initialized");
        }

        /// <summary>
        /// Enters configuration mode - enables window dragging and shows drag handles
        /// </summary>
        public void EnterConfigMode()
        {
            System.Diagnostics.Debug.WriteLine("[ConfigModeManager] Entering config mode");
            IsInConfigMode = true;
        }

        /// <summary>
        /// Exits configuration mode - disables window dragging and hides drag handles
        /// </summary>
        public void ExitConfigMode()
        {
            System.Diagnostics.Debug.WriteLine("[ConfigModeManager] Exiting config mode");
            IsInConfigMode = false;
        }
    }

    /// <summary>
    /// Event arguments for ConfigModeChanged event
    /// </summary>
    public class ConfigModeChangedEventArgs : EventArgs
    {
        /// <summary>
        /// Whether config mode is now active
        /// </summary>
        public bool IsInConfigMode { get; }

        public ConfigModeChangedEventArgs(bool isInConfigMode)
        {
            IsInConfigMode = isInConfigMode;
        }
    }
}