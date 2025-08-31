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
        "SessionLapsTotal", "SessionNum", "PlayerCarIdx", "SessionFlags"
    ])]
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields
        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly TelemetryDataBuilder _dataBuilder;
        private readonly SessionDataParser _sessionParser;
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
        public bool IsSessionDataReady => _sessionParser.IsDataReady;
        public bool IsConnected => _isConnected;
        public bool IsPrimed => _isConnected && _sessionParser.IsDataReady;
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
            _dataBuilder = new TelemetryDataBuilder(this);
            _sessionParser = new SessionDataParser();
            _sessionLogger = new SessionDataLogger(
                () => _sessionParser.GetCachedSessionYaml(),
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
                Console.WriteLine("[SVappsLAB] Starting initialization...");

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
                        Console.WriteLine("[SVappsLAB] Monitoring cancelled");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SVappsLAB] Monitoring error: {ex.Message}");
                    }
                });

                await Task.Delay(200);
                Console.WriteLine($"[SVappsLAB] Initialization complete.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Initialize error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> Start() => await Initialize();
        public bool StartSync() => Initialize().GetAwaiter().GetResult();

        public SVappsLABSnapshot GetSnapshot() => _latestSnapshot;
        public HashSet<string> GetSupportedFields() => TelemetryFieldRegistry.GetAllSupportedFields();
        public Dictionary<string, Type> GetFieldTypes() => new Dictionary<string, Type>(TelemetryFieldRegistry.FieldTypes);
        public bool SupportsField(string fieldName) => TelemetryFieldRegistry.IsFieldSupported(fieldName);

        public string DumpLatestYaml()
        {
            return _sessionParser.DumpSessionData(_sessionParser.GetCachedSessionYaml());
        }

        public string[] GetUserNames()
        {
            return _sessionParser.UserNames;
        }

        public string[] GetCarNumbers()
        {
            return _sessionParser.CarNumbers;
        }

        public int[] GetCarNumberRaw()
        {
            return _sessionParser.CarNumberRaw;
        }

        public int[] GetCarClassIDs()
        {
            return _sessionParser.CarClassIDs;
        }

        public bool[] GetCarIsAI()
        {
            return _sessionParser.CarIsAI;
        }

        public int[] GetCurDriverIncidentCount()
        {
            return _sessionParser.CurDriverIncidentCount;
        }

        public int GetIncidentLimit()
        {
            return _sessionParser.IncidentLimit;
        }

        // NEW: Session-specific data accessors
        public string GetSessionType()
        {
            return _sessionParser.SessionType;
        }

        public string GetSessionName()
        {
            return _sessionParser.SessionName;
        }

        public int[] GetQualifyResultsPositions()
        {
            return _sessionParser.QualifyResultsPositions;
        }

        public float[] GetQualifyResultsFastestTimes()
        {
            return _sessionParser.QualifyResultsFastestTimes;
        }

        public string GetCachedSessionYaml()
        {
            return _sessionParser.GetCachedSessionYaml();
        }

        private void OnConnectStateChanged(object sender, EventArgs e)
        {
            bool newConnectionState = _client != null && _client.IsConnected();

            if (newConnectionState != _isConnected)
            {
                _isConnected = newConnectionState;
                Console.WriteLine($"[SVappsLAB] Connection state changed: {_isConnected}");

                ConnectionStateChanged?.Invoke(_isConnected);

                if (!_isConnected)
                {
                    // Lost connection - stop retrying and clear data
                    StopYamlRetryTimer();
                    _sessionParser.ClearCache();
                    _lastSessionNumForLog = -1; // Reset logging tracking
                }
                else
                {
                    // Connected - start trying to get session data
                    Console.WriteLine("[SVappsLAB] Connected - will attempt to get session data");
                }

                CheckPrimedStateChange();
            }
        }

        private void CheckPrimedStateChange()
        {
            bool isPrimed = _isConnected && _sessionParser.IsDataReady;
            PrimedStateChanged?.Invoke(isPrimed);
        }

        private void OnSessionInfoUpdate(object sender, object e)
        {
            try
            {
                if (_client == null)
                {
                    Console.WriteLine("[SVappsLAB] ERROR: Client is null during session info update");
                    return;
                }

                // Get session data using the working method
                string sessionInfo = _client.GetRawTelemetrySessionInfoYaml();

                if (!string.IsNullOrEmpty(sessionInfo))
                {
                    // Got valid data - stop any retry timer and parse
                    StopYamlRetryTimer();

                    // Check if this is new data before parsing
                    if (_sessionParser.HasSessionDataChanged(sessionInfo))
                    {
                        Console.WriteLine($"[SVappsLAB] New session data received, length: {sessionInfo.Length}");

                        if (_sessionParser.ParseSessionData(sessionInfo))
                        {
                            System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Session data parsed successfully");
                            SessionYamlAvailable?.Invoke(sessionInfo);
                            SessionDataUpdated?.Invoke();
                            CheckPrimedStateChange();

                            // NEW: Check if this represents a session transition for logging
                            CheckForSessionTransitionLog();
                        }
                        else
                        {
                            Console.WriteLine("[SVappsLAB] Failed to parse session data - starting retry timer");
                            StartYamlRetryTimer();
                        }
                    }
                }
                else
                {
                    Console.WriteLine("[SVappsLAB] No session data received - starting retry timer");
                    StartYamlRetryTimer();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Error in OnSessionInfoUpdate: {ex.Message}");
                StartYamlRetryTimer(); // Retry on any error
            }
        }

        /// <summary>
        /// Check for session transitions and schedule dumps accordingly
        /// This is called from OnSessionInfoUpdate when session data changes
        /// </summary>
        private void CheckForSessionTransitionDump()
        {
            try
            {
                // Parse SessionNum from the newly parsed session data
                string sessionType = GetSessionType();
                int currentSessionNum = -1;

                // Map session type to session number for consistency
                if (sessionType.Contains("Practice") || sessionType.Contains("PRACTICE"))
                    currentSessionNum = 0;
                else if (sessionType.Contains("Qualify") || sessionType.Contains("QUALIFY"))
                    currentSessionNum = 1;
                else if (sessionType.Contains("Race") || sessionType.Contains("RACE"))
                    currentSessionNum = 2;

                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] CheckForSessionTransitionDump: SessionType='{sessionType}', Mapped SessionNum={currentSessionNum}, LastDumped={_lastSessionNumForLog}");

                // Only dump for valid sessions that we haven't dumped yet
                if (currentSessionNum >= 0 && currentSessionNum <= 2 && currentSessionNum != _lastSessionNumForLog)
                {
                    string sessionName = GetSessionName(currentSessionNum);
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Scheduling data log for {sessionName} (SessionNum {currentSessionNum})");

                    _sessionLogger.ScheduleLogForSession(currentSessionNum, sessionName);
                    _lastSessionNumForLog = currentSessionNum;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Not scheduling dump - SessionNum: {currentSessionNum}, LastDumped: {_lastSessionNumForLog}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error in CheckForSessionTransitionDump: {ex.Message}");
            }
        }

        /// <summary>
        /// Check if we should trigger a session data dump for debugging
        /// </summary>
        private void CheckForSessionDump()
        {
            System.Diagnostics.Debug.WriteLine("[SVappsLAB] CheckForSessionDump called");

            try
            {
                // Get session info from the latest snapshot or create a quick one
                if (_latestSnapshot != null)
                {
                    int currentSessionNum = _latestSnapshot.GetValue<int>("SessionNum", -1);
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Current SessionNum from snapshot: {currentSessionNum}, LastDumped: {_lastSessionNumForLog}");

                    // Only dump for practice, qualifying, and race sessions
                    // AND only when SessionNum actually changes
                    if (currentSessionNum >= 0 && currentSessionNum <= 2 && currentSessionNum != _lastSessionNumForLog)
                    {
                        string sessionType = GetSessionName(currentSessionNum);
                        System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Scheduling data dump for {sessionType} (SessionNum {currentSessionNum})");

                        _sessionLogger.ScheduleLogForSession(currentSessionNum, sessionType);
                        _lastSessionNumForLog = currentSessionNum; // Update AFTER scheduling
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Not scheduling dump - SessionNum: {currentSessionNum}, LastDumped: {_lastSessionNumForLog}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SVappsLAB] No latest snapshot available for dump check");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error checking for session dump: {ex.Message}");
                // Non-fatal, continue operation
            }
        }

        /// <summary>
        /// Check for session transitions and schedule logging accordingly
        /// This is called from OnSessionInfoUpdate when session data changes
        /// </summary>
        private void CheckForSessionTransitionLog()
        {
            try
            {
                // Use CurrentSessionNum from the proper session schedule parsing
                int currentSessionNum = _sessionParser.CurrentSessionNum;

                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] CheckForSessionTransitionLog: CurrentSessionNum={currentSessionNum}, LastLogged={_lastSessionNumForLog}");

                // Only log for valid sessions that we haven't logged yet
                if (currentSessionNum >= 0 && currentSessionNum <= 2 && currentSessionNum != _lastSessionNumForLog)
                {
                    string sessionType = _sessionParser.GetSessionTypeForNum(currentSessionNum);
                    string sessionName = _sessionParser.GetSessionNameForNum(currentSessionNum);

                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Scheduling data log for {sessionName} ({sessionType}, SessionNum {currentSessionNum})");

                    _sessionLogger.ScheduleLogForSession(currentSessionNum, sessionName);
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

        /// <summary>
        /// Convert SessionNum to readable name
        /// </summary>
        private string GetSessionName(int sessionNum)
        {
            return sessionNum switch
            {
                0 => "Practice",
                1 => "Qualifying",
                2 => "Race",
                _ => $"Session{sessionNum}"
            };
        }

        private void OnTelemetryUpdate(object sender, TelemetryData telemetryData)
        {
            try
            {
                var telemetryDict = _dataBuilder.BuildTelemetryDictionary(telemetryData);

                _latestSnapshot = new SVappsLABSnapshot(
                    telemetryDict,
                    _sessionParser.GetCachedSessionYaml(),
                    DateTime.UtcNow
                );

                SnapshotAvailable?.Invoke(_latestSnapshot);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Telemetry update error: {ex.Message}");
            }
        }

        private void StartYamlRetryTimer()
        {
            lock (_retryLock)
            {
                if (!_isRetryingYaml && _isConnected && !_sessionParser.IsDataReady)
                {
                    Console.WriteLine("[SVappsLAB] Starting YAML retry timer");
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
                    Console.WriteLine("[SVappsLAB] Stopping YAML retry timer");
                    _yamlRetryTimer.Stop();
                    _isRetryingYaml = false;
                }
            }
        }

        private void OnYamlRetryTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                // Don't retry if we already have data or aren't connected
                if (!_isConnected || _sessionParser.IsDataReady)
                {
                    StopYamlRetryTimer();
                    return;
                }

                Console.WriteLine("[SVappsLAB] Retrying YAML data retrieval...");
                string sessionInfo = _client?.GetRawTelemetrySessionInfoYaml();

                if (!string.IsNullOrEmpty(sessionInfo))
                {
                    Console.WriteLine($"[SVappsLAB] Retry successful - got session data, length: {sessionInfo.Length}");

                    if (_sessionParser.ParseSessionData(sessionInfo))
                    {
                        Console.WriteLine("[SVappsLAB] Retry parse successful");
                        SessionYamlAvailable?.Invoke(sessionInfo);
                        SessionDataUpdated?.Invoke();
                        CheckPrimedStateChange();

                        // NEW: Check for session dump after successful retry
                        CheckForSessionTransitionLog();

                        StopYamlRetryTimer(); // Success - stop retrying
                    }
                    else
                    {
                        Console.WriteLine("[SVappsLAB] Retry parse failed - will try again");
                    }
                }
                else
                {
                    Console.WriteLine("[SVappsLAB] Retry failed - no session data available yet");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Error during YAML retry: {ex.Message}");
            }
        }

        public void Shutdown()
        {
            try
            {
                Console.WriteLine("[SVappsLAB] Starting shutdown...");

                // Stop retry timer
                StopYamlRetryTimer();
                _yamlRetryTimer?.Dispose();

                // Dispose session logger
                _sessionLogger?.Dispose();

                _cancellationTokenSource?.Cancel();

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