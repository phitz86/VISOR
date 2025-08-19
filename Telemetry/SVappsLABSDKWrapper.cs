using Microsoft.Extensions.Logging;
using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VISOR.Telemetry
{
    // This attribute tells the SVappsLAB SDK source generator which telemetry variables to include
    [RequiredTelemetryVars([
        // Lap timing
        "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
        "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
        
        // Fuel and car state
        "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
        
        // Positioning arrays
        "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
        "CarIdxLap", "CarIdxLastLapTime",
        
        // Session info
        "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
        "SessionLapsTotal", "SessionNum",

        // Player specific
        "PlayerCarIdx"
    ])]

    /// <summary>
    /// Main wrapper for SVappsLAB iRacing Telemetry SDK.
    /// Coordinates between telemetry client, session parser, and state management.
    /// </summary>
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields

        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly SessionDataParser _sessionParser;
        private readonly TelemetryStateManager _stateManager;
        private readonly TelemetryDataBuilder _dataBuilder;

        private SVappsLABSnapshot _latestSnapshot;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;

        #endregion

        #region Public Properties

        public string Name => "SVappsLAB iRacingTelemetrySDK";
        public bool IsConnected => _stateManager?.IsConnected ?? false;
        public bool IsTelemetryActive => _stateManager?.IsTelemetryActive ?? false;
        public bool IsSessionDataReady => _sessionParser?.IsDataReady ?? false;
        public bool IsPrimed => _stateManager?.IsPrimed ?? false;
        public SessionDataParser SessionParser => _sessionParser;

        #endregion

        #region Events

        // Core telemetry events
        public event Action<SVappsLABSnapshot> SnapshotAvailable;
        public event Action<string> SessionYamlAvailable;

        // State change events
        public event Action<bool> ConnectionStateChanged;
        public event Action<bool> PrimedStateChanged;
        public event Action SessionDataUpdated;

        #endregion

        #region Constructor

        public SVappsLABSDKWrapper()
        {
            _logger = new NullLogger<SVappsLABSDKWrapper>();
            _sessionParser = new SessionDataParser();
            _dataBuilder = new TelemetryDataBuilder(_sessionParser);
            _stateManager = new TelemetryStateManager(_sessionParser);

            // Wire up state manager events
            _stateManager.ConnectionStateChanged += (connected) => ConnectionStateChanged?.Invoke(connected);
            _stateManager.PrimedStateChanged += OnPrimedStateChanged;
            _stateManager.SessionDataUpdated += () => SessionDataUpdated?.Invoke();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Initialize and start the telemetry connection
        /// </summary>
        public async Task<bool> Initialize()
        {
            try
            {
                Console.WriteLine("[SVappsLAB] Starting initialization...");

                // Create and configure the telemetry client
                _client = TelemetryClient<TelemetryData>.Create(_logger);
                _client.OnSessionInfoUpdate += OnSessionInfoUpdate;
                _client.OnTelemetryUpdate += OnTelemetryUpdate;
                _client.OnConnectStateChanged += OnConnectStateChanged;

                _cancellationTokenSource = new CancellationTokenSource();

                // Start the telemetry monitoring task
                _monitoringTask = Task.Run(async () =>
                {
                    try
                    {
                        await _client.Monitor(_cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("[SVappsLAB] Monitoring cancelled");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SVappsLAB] Monitoring error: {ex.Message}");
                    }
                });

                // Start the state manager's session polling
                await _stateManager.StartPolling(_client, _cancellationTokenSource.Token);

                // Also start a manual session check task
                _ = Task.Run(async () => await ManualSessionCheckLoop(_cancellationTokenSource.Token));

                await Task.Delay(200);

                Console.WriteLine($"[SVappsLAB] Initialization complete.");
                Console.WriteLine($"[SVappsLAB] Tracking {TelemetryFieldRegistry.GetSupportedFieldCount()} fields");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Initialize error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Manually check for session info since the event might not fire
        /// </summary>
        private async Task ManualSessionCheckLoop(CancellationToken cancellationToken)
        {
            await Task.Delay(2000); // Wait for connection to establish

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_client != null && IsConnected && !IsSessionDataReady)
                    {
                        System.Diagnostics.Debug.WriteLine("[SVappsLAB] Manual session check...");

                        // Try to manually trigger session info update
                        OnSessionInfoUpdate(_client, EventArgs.Empty);
                    }

                    // Check every 2 seconds until we have session data, then every 10 seconds
                    int delay = IsSessionDataReady ? 10000 : 2000;
                    await Task.Delay(delay, cancellationToken);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Manual check error: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        public async Task<bool> Start() => await Initialize();
        public bool StartSync() => Initialize().GetAwaiter().GetResult();

        /// <summary>
        /// Get a snapshot of current telemetry data
        /// </summary>
        public SVappsLABSnapshot GetSnapshot() => _latestSnapshot;

        /// <summary>
        /// Get all supported field names
        /// </summary>
        public HashSet<string> GetSupportedFields() => TelemetryFieldRegistry.GetAllSupportedFields();

        /// <summary>
        /// Get field type mappings
        /// </summary>
        public Dictionary<string, Type> GetFieldTypes() => new Dictionary<string, Type>(TelemetryFieldRegistry.FieldTypes);

        /// <summary>
        /// Check if a field is supported
        /// </summary>
        public bool SupportsField(string fieldName) => TelemetryFieldRegistry.IsFieldSupported(fieldName);

        /// <summary>
        /// Dump the latest YAML session data
        /// </summary>
        public string DumpLatestYaml() => _sessionParser.DumpSessionData(_latestSnapshot?.RawSessionData ?? "");

        /// <summary>
        /// Shutdown the SDK wrapper and clean up resources
        /// </summary>
        public void Shutdown()
        {
            try
            {
                Console.WriteLine("[SVappsLAB] Starting shutdown...");

                _cancellationTokenSource?.Cancel();
                _stateManager?.StopPolling();

                if (_monitoringTask != null && !_monitoringTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    Console.WriteLine("[SVappsLAB] Monitoring task did not shut down gracefully");
                }

                if (_client != null)
                {
                    _client.OnSessionInfoUpdate -= OnSessionInfoUpdate;
                    _client.OnTelemetryUpdate -= OnTelemetryUpdate;
                    _client.OnConnectStateChanged -= OnConnectStateChanged;
                    _client.Dispose();
                    _client = null;
                }

                _cancellationTokenSource?.Dispose();
                _sessionParser.ClearCache();

                Console.WriteLine("[SVappsLAB] Shutdown complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Shutdown error: {ex.Message}");
            }
        }

        #endregion

        #region Event Handlers

        private void OnConnectStateChanged(object sender, EventArgs e)
        {
            bool newConnectionState = _client != null && _client.IsConnected();
            _stateManager.UpdateConnectionState(newConnectionState);

            if (!newConnectionState)
            {
                _sessionParser.ClearCache();
            }
        }

        private void OnSessionInfoUpdate(object sender, object e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SVappsLAB] OnSessionInfoUpdate triggered!");

                string sessionInfo = ExtractSessionInfo();

                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Session info extracted, length: {sessionInfo?.Length ?? 0}");

                if (!string.IsNullOrEmpty(sessionInfo))
                {
                    // Log first 200 chars to see what we got
                    var preview = sessionInfo.Length > 200 ? sessionInfo.Substring(0, 200) : sessionInfo;
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Session preview: {preview}");

                    ProcessSessionInfo(sessionInfo);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] Session info was empty or null");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Session info error: {ex.Message}");
                Console.WriteLine($"[SVappsLAB] Session info error: {ex.Message}");
            }
        }

        private void OnTelemetryUpdate(object sender, TelemetryData telemetryData)
        {
            try
            {
                // Build the telemetry dictionary
                var telemetryDict = _dataBuilder.BuildTelemetryDictionary(telemetryData);

                // Create snapshot
                _latestSnapshot = new SVappsLABSnapshot(
                    telemetryDict,
                    _sessionParser.GetCachedSessionYaml(),
                    DateTime.UtcNow
                );

                // Update state manager
                _stateManager.UpdateTelemetryState(true);

                // Always emit snapshots - session data is optional enhancement, not required
                SnapshotAvailable?.Invoke(_latestSnapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Telemetry update error: {ex.Message}");
                Console.WriteLine($"[SVappsLAB] Telemetry update error: {ex.Message}");
            }
        }

        private void OnPrimedStateChanged(bool isPrimed)
        {
            PrimedStateChanged?.Invoke(isPrimed);

            // When we become primed, emit the latest snapshot
            if (isPrimed && _latestSnapshot != null)
            {
                Console.WriteLine("[SVappsLAB] Emitting first primed snapshot");
                SnapshotAvailable?.Invoke(_latestSnapshot);
            }
        }

        #endregion

        #region Private Methods

        private string ExtractSessionInfo()
        {
            try
            {
                var clientType = _client?.GetType();
                var sessionMethod = clientType?.GetMethod("GetSessionInfo") ??
                                  clientType?.GetMethod("SessionInfo") ??
                                  clientType?.GetMethod("GetSession");

                if (sessionMethod != null)
                {
                    var sessionResult = sessionMethod.Invoke(_client, null);
                    if (sessionResult != null)
                    {
                        var rawProperty = sessionResult.GetType().GetProperty("Raw");
                        var sessionInfo = rawProperty?.GetValue(sessionResult)?.ToString() ?? sessionResult.ToString();

                        // Debug output
                        if (!string.IsNullOrEmpty(sessionInfo))
                        {
                            System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Extracted session info, length: {sessionInfo.Length}");
                        }

                        return sessionInfo;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] ExtractSessionInfo error: {ex.Message}");
            }

            return string.Empty;
        }

        private void ProcessSessionInfo(string sessionInfo)
        {
            bool wasReady = _sessionParser.IsDataReady;
            bool parseSuccess = _sessionParser.ParseSessionData(sessionInfo);

            if (parseSuccess)
            {
                SessionYamlAvailable?.Invoke(sessionInfo);

                // Let state manager handle the state transitions
                _stateManager.NotifySessionDataParsed(wasReady);

                // If session was updated (not first time), re-emit snapshot
                if (wasReady && _latestSnapshot != null)
                {
                    SnapshotAvailable?.Invoke(_latestSnapshot);
                }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Shutdown();
        }

        #endregion
    }

    // Simple null logger implementation
    public class NullLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
    }
}