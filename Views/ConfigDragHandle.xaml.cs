using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VISOR.Diagnostics;
using VISOR.Settings;

namespace VISOR.Views
{
    /// <summary>
    /// The drag handle the overlay and the radar show in config mode: visible only while config
    /// mode is on, and dragging it moves its window. It reads the current mode when its window
    /// opens, so a window created while config mode is already on starts with its handle showing
    /// (B13), and it stops listening when its window closes.
    /// </summary>
    public partial class ConfigDragHandle : UserControl
    {
        private Window? _window;

        public ConfigDragHandle()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Loaded can come again on a re-layout; listen once per window.
            if (_window != null)
                return;

            _window = Window.GetWindow(this);
            if (_window == null)
                return;

            var configMode = ConfigModeManager.Instance;
            configMode.ConfigModeChanged += OnConfigModeChanged;
            _window.Closed += (_, _) => configMode.ConfigModeChanged -= OnConfigModeChanged;

            Visibility = configMode.IsInConfigMode ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnConfigModeChanged(bool inConfigMode)
        {
            // Raised by the Config window, on the UI thread; queued in case it ever isn't.
            if (Dispatcher.CheckAccess())
                ShowFor(inConfigMode);
            else
                Dispatcher.BeginInvoke(() => ShowFor(inConfigMode));
        }

        private void ShowFor(bool inConfigMode)
        {
            Log.Debug($"[{_window?.GetType().Name}] Config mode changed to: {inConfigMode}");
            Visibility = inConfigMode ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (ConfigModeManager.Instance.IsInConfigMode)
                _window?.DragMove();
        }
    }
}
