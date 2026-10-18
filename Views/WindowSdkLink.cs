using System.Windows;
using System.Windows.Threading;
using VISOR.Telemetry;

namespace VISOR.Views
{
    /// <summary>
    /// One window's connection to the SDK's events. Telemetry frames go through a
    /// <see cref="FramePoster"/>; connection and "HUD ready" changes are queued to the window's UI
    /// thread at the frames' priority (Send), so the SDK never waits on the UI thread (which,
    /// during shutdown, is itself waiting for the SDK to stop) and both windows handle every
    /// change the same way. The handlers run on the UI thread.
    ///
    /// <see cref="Attach"/> once the window has loaded; the link detaches itself when the window
    /// closes.
    /// </summary>
    internal sealed class WindowSdkLink
    {
        private readonly SVappsLABSDKWrapper _sdk;
        private readonly Dispatcher _dispatcher;
        private readonly FramePoster _framePoster;
        private readonly Action<bool> _onConnectionChanged;
        private readonly Action<bool> _onPrimedChanged;
        private bool _attached;

        public WindowSdkLink(Window window, SVappsLABSDKWrapper sdk, Action<SVappsLABSnapshot> onFrame,
            Action<bool> onConnectionChanged, Action<bool> onPrimedChanged)
        {
            _sdk = sdk;
            _dispatcher = window.Dispatcher;
            _framePoster = new FramePoster(_dispatcher, window.GetType().Name, onFrame);
            _onConnectionChanged = onConnectionChanged;
            _onPrimedChanged = onPrimedChanged;
            window.Closed += (_, _) => Detach();
        }

        public void Attach()
        {
            if (_attached) return;
            _sdk.SnapshotAvailable += OnSnapshotAvailable;
            _sdk.ConnectionStateChanged += OnConnectionStateChanged;
            _sdk.PrimedStateChanged += OnPrimedStateChanged;
            _attached = true;
        }

        private void Detach()
        {
            if (!_attached) return;
            _sdk.SnapshotAvailable -= OnSnapshotAvailable;
            _sdk.ConnectionStateChanged -= OnConnectionStateChanged;
            _sdk.PrimedStateChanged -= OnPrimedStateChanged;
            _attached = false;
        }

        // SDK threads: hand everything to the UI thread without waiting for it.
        private void OnSnapshotAvailable(SVappsLABSnapshot snapshot) => _framePoster.Post(snapshot);

        private void OnConnectionStateChanged(bool isConnected) =>
            _dispatcher.BeginInvoke(DispatcherPriority.Send, _onConnectionChanged, isConnected);

        private void OnPrimedStateChanged(bool isPrimed) =>
            _dispatcher.BeginInvoke(DispatcherPriority.Send, _onPrimedChanged, isPrimed);
    }
}
