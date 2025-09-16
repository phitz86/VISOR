using Microsoft.Extensions.Logging;
using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VISOR.Diagnostics;

namespace VISOR.Telemetry
{
    [RequiredTelemetryVars([
        "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
        "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
        "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
        "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
        "CarIdxLap", "CarIdxLastLapTime", "CarIdxOnPitRoad",
        "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
        "SessionLapsTotal", "SessionNum", "PlayerCarIdx", "SessionFlags",
        // Radar and F2 support fields
        "CarLeftRight", "CarIdxF2Time"
    ])]
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields
        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly TelemetryDataBuilder _dataBuilder;
        private readonly SessionDataCoordinator _sessionCoordinator;
        private readonly SessionDataLogger _sessionLogger;
        private SVappsLABSnapshot _latestSnapshot;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;
        private bool _isConnected = false;

        private System.Timers.Timer _yamlRetryTimer;
        private readonly object _retryLock = new();
        private bool _isRetryingYaml = false;

        private int _lastSessionNumForLog = -1;

        // Caches for efficient "log on change" logic
        private float[] _lastCarLeftRight = new float[64];
        private float[] _lastF2Time = new float[64];

        // Flags for one-time diagnostic logging
        private static bool _clrFieldNotFoundLogged = false;
        private static bool _clrValueIsNullLogged = false;
        private static bool _clrTypeCastFailedLogged = false;
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
        public event Action<string> SessionYamlAvailable;
        public event Action<bool> ConnectionStateChanged;
        public event Action<bool> PrimedStateChanged;
        public event Action SessionDataUpdated;
        #endregion

        public SVappsLABSDKWrapper()
        {
            _logger = new NullLogger<SVappsLABSDKWrapper>();
            _sessionCoordinator = new SessionDataCoordinator();
            _dataBuilder = new TelemetryDataBuilder(_sessionCoordinator);
            _sessionLogger = new SessionDataLogger(
                () => _sessionCoordinator.GetCachedSessionYaml(),
                () => GetFieldTypes()
            );

            _yamlRetryTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _yamlRetryTimer.Elapsed += OnYamlRetryTimer;
        }

        public async Task<bool> Initialize()
        {
            try
            {
                _client = TelemetryClient<TelemetryData>.Create(_logger);
                _client.OnSessionInfoUpdate += OnSessionInfoUpdate;
                _client.OnTelemetryUpdate += OnTelemetryUpdate;
                _client.OnConnectStateChanged += OnConnectStateChanged;

                _cancellationTokenSource = new CancellationTokenSource();
                _monitoringTask = Task.Run(() => _client.Monitor(_cancellationTokenSource.Token));

                await Task.Delay(200);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Initialize error: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Connection state changed: {_isConnected}");
            ConnectionStateChanged?.Invoke(_isConnected);

            if (!_isConnected)
            {
                StopYamlRetryTimer();
                _sessionCoordinator.ClearCache();
                _lastSessionNumForLog = -1;
                Array.Clear(_lastCarLeftRight, 0, _lastCarLeftRight.Length);
                Array.Clear(_lastF2Time, 0, _lastF2Time.Length);

                // Reset diagnostic flags for the next connection
                _clrFieldNotFoundLogged = false;
                _clrValueIsNullLogged = false;
                _clrTypeCastFailedLogged = false;
            }
            CheckPrimedStateChange();
        }

        private void CheckPrimedStateChange()
        {
            bool isPrimed = _isConnected && _sessionCoordinator.IsDataReady;
            PrimedStateChanged?.Invoke(isPrimed);
        }

        private void OnSessionInfoUpdate(object sender, object e)
        {
            try
            {
                string sessionInfo = _client?.GetRawTelemetrySessionInfoYaml();
                if (string.IsNullOrEmpty(sessionInfo))
                {
                    StartYamlRetryTimer();
                    return;
                }

                StopYamlRetryTimer();
                if (_sessionCoordinator.HasSessionDataChanged(sessionInfo))
                {
                    if (_sessionCoordinator.ParseSessionData(sessionInfo))
                    {
                        SessionYamlAvailable?.Invoke(sessionInfo);
                        SessionDataUpdated?.Invoke();
                        CheckPrimedStateChange();
                        CheckForSessionTransitionLog();
                    }
                    else
                    {
                        StartYamlRetryTimer();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error in OnSessionInfoUpdate: {ex.Message}");
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

                _sessionLogger.ScheduleSessionAwareLogs(currentSessionNum, sessionName, sessionTimeSeconds);
                _lastSessionNumForLog = currentSessionNum;
            }
        }

        private void OnTelemetryUpdate(object sender, TelemetryData telemetryData)
        {
            // --- CarLeftRight Logging Block ---
            try
            {
                var clrField = typeof(TelemetryData).GetField("CarLeftRight");
                if (clrField == null)
                {
                    if (!_clrFieldNotFoundLogged)
                    {
                        System.Diagnostics.Debug.WriteLine("[DIAGNOSTIC] CarLeftRight field was NOT FOUND via reflection.");
                        _clrFieldNotFoundLogged = true;
                    }
                }
                else if (clrField.GetValue(telemetryData) is float[] carLeftRight)
                {
                    int playerCarIdx = telemetryData.PlayerCarIdx;
                    var lapDistPct = telemetryData.CarIdxLapDistPct;
                    for (int i = 0; i < carLeftRight.Length; i++)
                    {
                        if (i != playerCarIdx && carLeftRight[i] != _lastCarLeftRight[i])
                        {
                            if (carLeftRight[i] != 0f)
                            {
                                _sessionLogger.LogTelemetryFrame(
                                    telemetryData.SessionTime, playerCarIdx, i, carLeftRight[i],
                                    lapDistPct[playerCarIdx], lapDistPct[i]);
                            }
                            _lastCarLeftRight[i] = carLeftRight[i];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error during CarLeftRight logging: {ex.Message}");
            }

            // --- F2Time Logging Block ---
            try
            {
                var f2Field = typeof(TelemetryData).GetField("CarIdxF2Time");
                if (f2Field != null && f2Field.GetValue(telemetryData) is float[] f2Time)
                {
                    int playerCarIdx = telemetryData.PlayerCarIdx;
                    var lapDistPct = telemetryData.CarIdxLapDistPct;
                    for (int i = 0; i < f2Time.Length; i++)
                    {
                        if (i != playerCarIdx && f2Time[i] != _lastF2Time[i])
                        {
                            if (f2Time[i] != 0f)
                            {
                                _sessionLogger.LogF2TimeFrame(
                                   telemetryData.SessionTime, playerCarIdx, i, f2Time[i],
                                   lapDistPct[playerCarIdx], lapDistPct[i]);
                            }
                            _lastF2Time[i] = f2Time[i];
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error during F2Time logging: {ex.Message}");
            }

            // --- Core Telemetry Processing ---
            try
            {
                var telemetryDict = _dataBuilder.BuildTelemetryDictionary(telemetryData);
                _latestSnapshot = new SVappsLABSnapshot(
                    telemetryDict,
                    _sessionCoordinator.GetCachedSessionYaml(),
                    DateTime.UtcNow
                );
                SnapshotAvailable?.Invoke(_latestSnapshot);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Telemetry update error: {ex.Message}");
            }
        }

        private void StartYamlRetryTimer()
        {
            lock (_retryLock)
            {
                if (!_isRetryingYaml && _isConnected && !_sessionCoordinator.IsDataReady)
                {
                    _isRetryingYaml = true;
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
            OnSessionInfoUpdate(this, EventArgs.Empty);
        }

        public void Shutdown()
        {
            try
            {
                StopYamlRetryTimer();
                _yamlRetryTimer?.Dispose();
                _sessionLogger?.Dispose();
                _cancellationTokenSource?.Cancel();

                if (_monitoringTask != null && !_monitoringTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] Monitoring task did not shut down gracefully");
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Shutdown error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Shutdown();
        }
    }

    public class NullLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
    }
}