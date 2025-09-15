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
        // Radar support fields
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

        // YAML retry logic
        private System.Timers.Timer _yamlRetryTimer;
        private readonly object _retryLock = new();
        private bool _isRetryingYaml = false;

        // Session logging tracking
        private int _lastSessionNumForLog = -1;
        #endregion

        #region Public Properties
        public string Name => "SVappsLAB iRacingTelemetrySDK";
        public bool IsSessionDataReady => _sessionCoordinator.IsDataReady;
        public bool IsConnected => _isConnected;
        public bool IsPrimed => _isConnected && _sessionCoordinator.IsDataReady;

        // Expose the coordinator for other parts of the app to use
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

            System.Diagnostics.Debug.WriteLine("[SVappsLAB] SVappsLABSDKWrapper constructor completed");

            // Initialize retry timer (but don't start it yet)
            _yamlRetryTimer = new System.Timers.Timer(1000); // 1 second interval
            _yamlRetryTimer.Elapsed += OnYamlRetryTimer;
            _yamlRetryTimer.AutoReset = true;
        }

        public async Task<bool> Initialize()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SVappsLAB] Starting initialization...");

                _client = TelemetryClient<TelemetryData>.Create(_logger);
                _client.OnSessionInfoUpdate += OnSessionInfoUpdate;
                _client.OnTelemetryUpdate += OnTelemetryUpdate;
                _client.OnConnectStateChanged += OnConnectStateChanged;

                _cancellationTokenSource = new CancellationTokenSource();

                _monitoringTask = Task.Run(async () =>
                {
                    try
                    {
                        await _client.Monitor(_cancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine("[SVappsLAB] Monitoring cancelled");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Monitoring error: {ex.Message}");
                    }
                });

                await Task.Delay(200);
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Initialization complete.");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Initialize error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> Start() => await Initialize();
        public bool StartSync() => Initialize().GetAwaiter().GetResult();

        public SVappsLABSnapshot GetSnapshot() => _latestSnapshot;
        public HashSet<string> GetSupportedFields() => TelemetryFieldRegistry.GetAllSupportedFields();
        public Dictionary<string, Type> GetFieldTypes() => new Dictionary<string, Type>(TelemetryFieldRegistry.FieldTypes);
        public bool SupportsField(string fieldName) => TelemetryFieldRegistry.IsFieldSupported(fieldName);

        public void LogCurrentSessionData()
        {
            // Trigger immediate logging via SessionDataLogger
            int currentSession = _sessionCoordinator.CurrentSessionNum;
            string sessionType = _sessionCoordinator.GetCurrentSessionType();

            System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Manual log request for Session {currentSession} ({sessionType})");
            _sessionLogger.ScheduleLogForSession(currentSession, sessionType);
        }

        private void OnConnectStateChanged(object sender, EventArgs e)
        {
            bool newConnectionState = _client != null && _client.IsConnected();

            if (newConnectionState != _isConnected)
            {
                _isConnected = newConnectionState;
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Connection state changed: {_isConnected}");

                ConnectionStateChanged?.Invoke(_isConnected);

                if (!_isConnected)
                {
                    // Lost connection - stop retrying and clear data
                    StopYamlRetryTimer();
                    _sessionCoordinator.ClearCache();
                    _lastSessionNumForLog = -1; // Reset logging tracking
                }
                else
                {
                    // Connected - start trying to get session data
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] Connected - will attempt to get session data");
                }

                CheckPrimedStateChange();
            }
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
                if (_client == null)
                {
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] ERROR: Client is null during session info update");
                    return;
                }

                string sessionInfo = _client.GetRawTelemetrySessionInfoYaml();

                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Raw YAML length: {sessionInfo?.Length ?? 0}");

                if (!string.IsNullOrEmpty(sessionInfo))
                {
                    // Log first few lines of YAML to see what we're getting
                    var lines = sessionInfo.Split('\n');
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] First 5 YAML lines:");
                    for (int i = 0; i < Math.Min(5, lines.Length); i++)
                    {
                        System.Diagnostics.Debug.WriteLine($"  {i}: {lines[i]}");
                    }

                    StopYamlRetryTimer();

                    if (_sessionCoordinator.HasSessionDataChanged(sessionInfo))
                    {
                        System.Diagnostics.Debug.WriteLine($"[SVappsLAB] New session data received, length: {sessionInfo.Length}");
                        System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Parsing session data...");

                        if (_sessionCoordinator.ParseSessionData(sessionInfo))
                        {
                            System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Session data parsed successfully");
                            SessionYamlAvailable?.Invoke(sessionInfo);
                            SessionDataUpdated?.Invoke();
                            CheckPrimedStateChange();
                            CheckForSessionTransitionLog();
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[SVappsLAB] FAILED to parse session data - starting retry timer");
                            StartYamlRetryTimer();
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[SVappsLAB] Session data unchanged");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] No session data received - starting retry timer");
                    StartYamlRetryTimer();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error in OnSessionInfoUpdate: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Stack trace: {ex.StackTrace}");
                StartYamlRetryTimer();
            }
        }

        private void CheckForSessionTransitionLog()
        {
            try
            {
                int currentSessionNum = _sessionCoordinator.CurrentSessionNum;
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] CheckForSessionTransitionLog: CurrentSessionNum={currentSessionNum}, LastLogged={_lastSessionNumForLog}");

                if (currentSessionNum >= 0 && currentSessionNum <= 2 && currentSessionNum != _lastSessionNumForLog)
                {
                    string sessionType = _sessionCoordinator.GetSessionType(currentSessionNum);
                    string sessionName = _sessionCoordinator.GetSessionName(currentSessionNum);
                    double sessionTimeSeconds = GetSessionTimeSeconds(currentSessionNum);

                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Scheduling session-aware log for {sessionName} ({sessionType}, SessionNum {currentSessionNum}, Duration: {sessionTimeSeconds}s)");

                    _sessionLogger.ScheduleSessionAwareLogs(currentSessionNum, sessionName, sessionTimeSeconds);
                    _lastSessionNumForLog = currentSessionNum;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Not scheduling log - SessionNum: {currentSessionNum}, LastLogged: {_lastSessionNumForLog}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error in CheckForSessionTransitionLog: {ex.Message}");
            }
        }

        private double GetSessionTimeSeconds(int sessionNum)
        {
            try
            {
                double sessionTimeSeconds = _sessionCoordinator.GetSessionTimeSeconds(sessionNum);

                if (sessionTimeSeconds > 0)
                {
                    return sessionTimeSeconds;
                }
                else
                {
                    string sessionType = _sessionCoordinator.GetSessionType(sessionNum);
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] No session duration found, using fallback for {sessionType}");

                    return sessionType.ToLowerInvariant() switch
                    {
                        var type when type.Contains("practice") => 1200.0,
                        var type when type.Contains("qualify") => 900.0,
                        var type when type.Contains("race") => 1800.0,
                        _ => 900.0
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error getting session time: {ex.Message}");
                return 900.0;
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
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] Starting YAML retry timer");
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
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] Stopping YAML retry timer");
                    _yamlRetryTimer.Stop();
                    _isRetryingYaml = false;
                }
            }
        }

        private void OnYamlRetryTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (!_isConnected || _sessionCoordinator.IsDataReady)
                {
                    StopYamlRetryTimer();
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[SVappsLAB] Retrying YAML data retrieval...");
                string sessionInfo = _client?.GetRawTelemetrySessionInfoYaml();

                if (!string.IsNullOrEmpty(sessionInfo))
                {
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Retry successful - got session data, length: {sessionInfo.Length}");

                    if (_sessionCoordinator.ParseSessionData(sessionInfo))
                    {
                        System.Diagnostics.Debug.WriteLine("[SVappsLAB] Retry parse successful");
                        SessionYamlAvailable?.Invoke(sessionInfo);
                        SessionDataUpdated?.Invoke();
                        CheckPrimedStateChange();
                        CheckForSessionTransitionLog();
                        StopYamlRetryTimer();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[SVappsLAB] Retry parse failed - will try again");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] Retry failed - no session data available yet");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error during YAML retry: {ex.Message}");
            }
        }

        public void Shutdown()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SVappsLAB] Starting shutdown...");

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
                    _client = null;
                }

                _cancellationTokenSource?.Dispose();
                _sessionCoordinator.ClearCache();

                System.Diagnostics.Debug.WriteLine("[SVappsLAB] Shutdown complete");
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