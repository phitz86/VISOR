using Microsoft.Extensions.Logging;
using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VISOR.Diagnostics;
using VISOR.ViewModels;

namespace VISOR.Telemetry
{
    [RequiredTelemetryVars([
        "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
        "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
        "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
        "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
        "CarIdxLap", "CarIdxLastLapTime", "CarIdxBestLapTime", "CarIdxOnPitRoad",
        "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
        "SessionLapsTotal", "SessionNum", "PlayerCarIdx", "SessionFlags",
        "CarLeftRight", "CarIdxF2Time", "CarIdxEstTime", "CarIdxLapCompleted"
    ])]
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields
        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly TelemetryDataBuilder _dataBuilder;
        private readonly SessionDataCoordinator _sessionCoordinator;
#if DEBUG
        private readonly SessionDataLogger _sessionLogger;
        private readonly TelemetryCSVLogger _telemetryLogger;
#endif
        private SVappsLABSnapshot _latestSnapshot;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;
        private bool _isConnected = false;

        private System.Timers.Timer _yamlRetryTimer;
        private readonly object _retryLock = new();
        private bool _isRetryingYaml = false;
        private int _yamlRetryCount = 0;

        private int _lastSessionNumForLog = -1;
        private bool _lastPrimedState = false;
        private DateTime? _disconnectedAt = null;
        #endregion

        #region Public Properties
        public string Name => "SVappsLAB iRacingTelemetrySDK";
        public bool IsSessionDataReady => _sessionCoordinator.IsDataReady;
        public bool IsConnected => _isConnected;
        public bool IsPrimed => _isConnected && _sessionCoordinator.IsDataReady;
        public SessionDataCoordinator Coordinator => _sessionCoordinator;
        #endregion

        #region Events
        public event Action<SVappsLABSnapshot> SnapshotAvailable;
        public event Action<bool> ConnectionStateChanged;
        public event Action<bool> PrimedStateChanged;
        #endregion

        public SVappsLABSDKWrapper()
        {
            _logger = new VisorSdkLogger<SVappsLABSDKWrapper>();
            _sessionCoordinator = new SessionDataCoordinator();
            _dataBuilder = new TelemetryDataBuilder(_sessionCoordinator);

#if DEBUG
            _sessionLogger = new SessionDataLogger(
                () => _sessionCoordinator.GetCachedSessionYaml(),
                () => GetFieldTypes()
            );
            _telemetryLogger = new TelemetryCSVLogger();
#endif

            _yamlRetryTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _yamlRetryTimer.Elapsed += OnYamlRetryTimer;
        }

        public async Task<bool> Initialize()
        {
            try
            {
                Log.Info("SVappsLAB SDK initialization started");

                _client = TelemetryClient<TelemetryData>.Create(_logger);
                _client.OnSessionInfoUpdate += OnSessionInfoUpdate;
                _client.OnTelemetryUpdate += OnTelemetryUpdate;
                _client.OnConnectStateChanged += OnConnectStateChanged;

                _cancellationTokenSource = new CancellationTokenSource();
                _monitoringTask = Task.Run(() => _client.Monitor(_cancellationTokenSource.Token));

                await Task.Delay(200);
                Log.Info("SVappsLAB SDK initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("SVappsLAB SDK initialization error", ex);
                return false;
            }
        }

        public SVappsLABSnapshot GetSnapshot() => _latestSnapshot;
        public HashSet<string> GetSupportedFields() => TelemetryFieldRegistry.GetAllSupportedFields();
        public Dictionary<string, Type> GetFieldTypes() => new Dictionary<string, Type>(TelemetryFieldRegistry.FieldTypes);
        public bool SupportsField(string fieldName) => TelemetryFieldRegistry.IsFieldSupported(fieldName);

        private void OnConnectStateChanged(object sender, EventArgs e)
        {
            bool newConnectionState = _client?.IsConnected() ?? false;
            if (newConnectionState == _isConnected) return;

            _isConnected = newConnectionState;

            if (_isConnected && _disconnectedAt.HasValue)
            {
                var duration = DateTime.UtcNow - _disconnectedAt.Value;
                Log.Info($"iRacing reconnected after {duration.TotalSeconds:F1}s disconnection");
                _disconnectedAt = null;
            }
            else
            {
                Log.Info($"iRacing connection state changed: {(_isConnected ? "Connected" : "Disconnected")}");
                if (!_isConnected)
                    _disconnectedAt = DateTime.UtcNow;
            }

            ConnectionStateChanged?.Invoke(_isConnected);

            if (!_isConnected)
            {
                StopYamlRetryTimer();
                _sessionCoordinator.ClearCache();
                _lastSessionNumForLog = -1;
            }
            CheckPrimedStateChange();
        }

        private void CheckPrimedStateChange()
        {
            bool isPrimed = _isConnected && _sessionCoordinator.IsDataReady;
            if (isPrimed != _lastPrimedState)
            {
                Log.Info(isPrimed
                    ? "HUD ready: iRacing connected and session data parsed"
                    : "HUD no longer primed: waiting for connection or session data");
                _lastPrimedState = isPrimed;
            }
            PrimedStateChanged?.Invoke(isPrimed);
        }

        private void OnSessionInfoUpdate(object sender, object e)
        {
            try
            {
                string sessionInfo = _client?.GetRawTelemetrySessionInfoYaml();
                if (string.IsNullOrEmpty(sessionInfo))
                {
                    Log.Warning("Session YAML is empty, retrying...");
                    StartYamlRetryTimer();
                    return;
                }

                StopYamlRetryTimer();
                if (_sessionCoordinator.HasSessionDataChanged(sessionInfo))
                {
                    if (_sessionCoordinator.ParseSessionData(sessionInfo))
                    {
                        Log.Debug("Session YAML retrieved and parsed successfully");
                        CheckPrimedStateChange();
                        CheckForSessionTransitionLog();
                    }
                    else
                    {
                        Log.Warning("Failed to parse session YAML, retrying...");
                        StartYamlRetryTimer();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error in OnSessionInfoUpdate", ex);
                StartYamlRetryTimer();
            }
        }

        private void CheckForSessionTransitionLog()
        {
            int currentSessionNum = _sessionCoordinator.CurrentSessionNum;
            if (currentSessionNum >= 0 && currentSessionNum != _lastSessionNumForLog)
            {
                string sessionType = _sessionCoordinator.GetSessionType(currentSessionNum);
                string sessionName = _sessionCoordinator.GetSessionName(currentSessionNum);
                double sessionTimeSeconds = _sessionCoordinator.GetSessionTimeSeconds(currentSessionNum);

                Log.Info($"Session transition: {sessionName} (Type: {sessionType}, Duration: {sessionTimeSeconds}s)");

#if DEBUG
                _sessionLogger?.ScheduleSessionAwareLogs(currentSessionNum, sessionName, sessionTimeSeconds);
#endif
                _lastSessionNumForLog = currentSessionNum;
            }
        }

        private void OnTelemetryUpdate(object sender, TelemetryData telemetryData)
        {
            try
            {
                var telemetryDict = _dataBuilder.BuildTelemetryDictionary(telemetryData);
                _latestSnapshot = new SVappsLABSnapshot(
                    telemetryDict,
                    _sessionCoordinator.GetCachedSessionYaml(),
                    DateTime.UtcNow
                );

#if DEBUG
                // Log telemetry data for analysis (internally throttled to 1Hz)
                _telemetryLogger?.LogSnapshot(_latestSnapshot, _sessionCoordinator);
#endif

                SnapshotAvailable?.Invoke(_latestSnapshot);
            }
            catch (Exception ex)
            {
                Log.Error("Telemetry update error", ex);
            }
        }

        private void StartYamlRetryTimer()
        {
            lock (_retryLock)
            {
                if (!_isRetryingYaml && _isConnected && !_sessionCoordinator.IsDataReady)
                {
                    _isRetryingYaml = true;
                    _yamlRetryCount = 0;
                    _yamlRetryTimer.Start();
                }
            }
        }

        private void StopYamlRetryTimer()
        {
            lock (_retryLock)
            {
                if (_isRetryingYaml)
                {
                    _yamlRetryTimer.Stop();
                    _isRetryingYaml = false;
                    _yamlRetryCount = 0;
                }
            }
        }

        private void OnYamlRetryTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_isConnected || _sessionCoordinator.IsDataReady)
            {
                StopYamlRetryTimer();
                return;
            }
            _yamlRetryCount++;
            if (_yamlRetryCount == 10)
                Log.Error($"Session YAML still empty after 10 retries — possible iRacing issue");
            OnSessionInfoUpdate(this, EventArgs.Empty);
        }

        public void Shutdown()
        {
            try
            {
                Log.Info("SVappsLAB SDK shutdown initiated");
                StopYamlRetryTimer();
                _yamlRetryTimer?.Dispose();

#if DEBUG
                _sessionLogger?.Dispose();
                _telemetryLogger?.Dispose();
#endif

                _cancellationTokenSource?.Cancel();

                if (_monitoringTask != null && !_monitoringTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    Log.Warning("Monitoring task did not shut down gracefully");
                }

                if (_client != null)
                {
                    _client.OnSessionInfoUpdate -= OnSessionInfoUpdate;
                    _client.OnTelemetryUpdate -= OnTelemetryUpdate;
                    _client.OnConnectStateChanged -= OnConnectStateChanged;
                    _client.Dispose();
                }
                _cancellationTokenSource?.Dispose();
                _sessionCoordinator.ClearCache();
                Log.Info("SVappsLAB SDK shutdown complete");
            }
            catch (Exception ex)
            {
                Log.Error("SVappsLAB SDK shutdown error", ex);
            }
        }

        public void Dispose()
        {
            Shutdown();
        }
    }

    /// <summary>
    /// Bridges Microsoft.Extensions.Logging output from the SVappsLAB SDK into VISOR's Log.cs.
    /// Warnings and errors are always surfaced; Info/Debug are forwarded when VISOR debug mode is on.
    /// </summary>
    public class VisorSdkLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning || Diagnostics.Log.DebugModeEnabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (formatter == null) return;

            string message = $"[SDK] {formatter(state, exception)}";

            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error:
                    Diagnostics.Log.Error(message, exception);
                    break;
                case LogLevel.Warning:
                    Diagnostics.Log.Warning(exception == null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})");
                    break;
                case LogLevel.Information:
                    Diagnostics.Log.Info(message);
                    break;
                default:
                    Diagnostics.Log.Debug(message);
                    break;
            }
        }
    }
}