using System;
using System.Threading;
using System.Threading.Tasks;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Manages telemetry connection state and coordinates readiness between telemetry and session data.
    /// </summary>
    public class TelemetryStateManager
    {
        private readonly SessionDataParser _sessionParser;
        private readonly object _stateLock = new object();

        // State tracking
        private bool _isConnected;
        private bool _telemetryActive;
        private int _lastSessionUpdateSeq = -1;
        private bool? _lastPrimedState = null;
        private Task _pollingTask;

        // Events
        public event Action<bool> ConnectionStateChanged;
        public event Action<bool> PrimedStateChanged;
        public event Action SessionDataUpdated;

        // Properties
        public bool IsConnected => _isConnected;
        public bool IsTelemetryActive => _telemetryActive;
        public bool IsPrimed => _telemetryActive && _sessionParser.IsDataReady;

        public TelemetryStateManager(SessionDataParser sessionParser)
        {
            _sessionParser = sessionParser ?? throw new ArgumentNullException(nameof(sessionParser));
        }

        /// <summary>
        /// Start polling for session updates
        /// </summary>
        public async Task StartPolling(object telemetryClient, CancellationToken cancellationToken)
        {
            _pollingTask = Task.Run(async () => await SessionPollingLoop(telemetryClient, cancellationToken));
        }

        /// <summary>
        /// Stop the polling task
        /// </summary>
        public void StopPolling()
        {
            // Polling will stop via cancellation token
            if (_pollingTask != null && !_pollingTask.Wait(TimeSpan.FromSeconds(1)))
            {
                Console.WriteLine("[StateManager] Polling task did not stop gracefully");
            }
        }

        /// <summary>
        /// Update the connection state
        /// </summary>
        public void UpdateConnectionState(bool isConnected)
        {
            lock (_stateLock)
            {
                if (_isConnected != isConnected)
                {
                    _isConnected = isConnected;
                    Console.WriteLine($"[StateManager] Connection state changed: {_isConnected}");
                    ConnectionStateChanged?.Invoke(_isConnected);

                    if (!_isConnected)
                    {
                        _telemetryActive = false;
                        CheckPrimedState();
                    }
                }
            }
        }

        /// <summary>
        /// Update the telemetry active state
        /// </summary>
        public void UpdateTelemetryState(bool isActive)
        {
            lock (_stateLock)
            {
                if (_telemetryActive != isActive)
                {
                    _telemetryActive = isActive;
                    Console.WriteLine($"[StateManager] Telemetry active: {_telemetryActive}");
                    CheckPrimedState();
                }
            }
        }

        /// <summary>
        /// Notify that session data was parsed
        /// </summary>
        public void NotifySessionDataParsed(bool wasReadyBefore)
        {
            if (!wasReadyBefore && _sessionParser.IsDataReady)
            {
                // First time ready
                Console.WriteLine("[StateManager] Session data now ready - checking primed state");
                CheckPrimedState();
            }
            else if (wasReadyBefore && _sessionParser.IsDataReady)
            {
                // Session data updated (driver joined/left)
                Console.WriteLine("[StateManager] Session data updated");
                SessionDataUpdated?.Invoke();
            }
        }

        /// <summary>
        /// Check and notify about primed state changes
        /// </summary>
        private void CheckPrimedState()
        {
            bool currentPrimed = IsPrimed;

            if (_lastPrimedState != currentPrimed)
            {
                _lastPrimedState = currentPrimed;
                Console.WriteLine($"[StateManager] Primed state changed: {currentPrimed}");
                PrimedStateChanged?.Invoke(currentPrimed);
            }
        }

        /// <summary>
        /// Background loop to poll for session updates
        /// </summary>
        private async Task SessionPollingLoop(object telemetryClient, CancellationToken cancellationToken)
        {
            Console.WriteLine("[StateManager] Session polling started");
            System.Diagnostics.Debug.WriteLine("[StateManager] Session polling started");

            // Give the SDK a moment to initialize
            await Task.Delay(1000, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Poll faster when waiting for initial data
                    int pollInterval = IsPrimed ? 2000 : 250;

                    if (_isConnected && telemetryClient != null)
                    {
                        // Force a session info check by calling OnSessionInfoUpdate through reflection
                        // This is needed because the SDK might not fire the event on its own initially
                        var onSessionInfoMethod = telemetryClient.GetType().GetMethod("OnSessionInfoUpdate",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                        if (onSessionInfoMethod != null)
                        {
                            System.Diagnostics.Debug.WriteLine("[StateManager] Forcing session info update check");
                            // This should trigger the wrapper's OnSessionInfoUpdate handler
                            onSessionInfoMethod.Invoke(telemetryClient, new object[] { telemetryClient, EventArgs.Empty });
                        }
                    }

                    await Task.Delay(pollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[StateManager] Polling error: {ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }

            System.Diagnostics.Debug.WriteLine("[StateManager] Session polling stopped");
        }
    }
}