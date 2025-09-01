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
    public class SVappsLABSDKWrapper : IDisposable, VISOR.ViewModels.ISessionDataProvider
    {
        #region Private Fields
        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly TelemetryDataBuilder _dataBuilder;
        private readonly SessionDataCoordinator _sessionCoordinator; // NEW: Replaced SessionDataParser
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
        public bool IsSessionDataReady => _sessionCoordinator.IsDataReady; // Updated
        public bool IsConnected => _isConnected;
        public bool IsPrimed => _isConnected && _sessionCoordinator.IsDataReady; // Updated
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
            _sessionCoordinator = new SessionDataCoordinator(); // NEW: Using coordinator
            _sessionLogger = new SessionDataLogger(
                () => _sessionCoordinator.GetCachedSessionYaml(), // Updated
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

        // ========== File Logging Methods ==========
        // All file output handled by SessionDataLogger - only console debug messages here

        public void LogCurrentSessionData()
        {
            // Trigger immediate logging via SessionDataLogger
            int currentSession = _sessionCoordinator.CurrentSessionNum;
            string sessionType = _sessionCoordinator.GetCurrentSessionType();

            Console.WriteLine($"[SVappsLAB] Manual log request for Session {currentSession} ({sessionType})");
            _sessionLogger.ScheduleLogForSession(currentSession, sessionType);
        }

        // ========== ISessionDataProvider Implementation ==========

        public bool IsDataReady => _sessionCoordinator.IsDataReady;
        public string[] UserNames => _sessionCoordinator.UserNames;
        public string[] CarNumbers => _sessionCoordinator.CarNumbers;
        public int[] CarNumberRaw => _sessionCoordinator.CarNumberRaw;
        public int[] CarClassIDs => _sessionCoordinator.CarClassIDs;
        public bool[] CarIsAI => _sessionCoordinator.CarIsAI;
        public int[] CurDriverIncidentCount => _sessionCoordinator.CurDriverIncidentCount;
        public int IncidentLimit => _sessionCoordinator.IncidentLimit;

        // ========== ISessionDataProvider Proxy Methods (for backward compatibility) ==========
        // These methods proxy calls to the coordinator to maintain backward compatibility

        public string[] GetUserNames() => UserNames;
        public string[] GetCarNumbers() => CarNumbers;
        public int[] GetCarNumberRaw() => CarNumberRaw;
        public int[] GetCarClassIDs() => CarClassIDs;
        public bool[] GetCarIsAI() => CarIsAI;
        public int[] GetCurDriverIncidentCount() => CurDriverIncidentCount;
        public int GetIncidentLimit() => IncidentLimit;

        // ========== Enhanced Session-Aware Methods ==========

        public string GetSessionType() => _sessionCoordinator.GetCurrentSessionType();
        public string GetSessionName() => _sessionCoordinator.GetCurrentSessionName();
        public int GetCurrentSessionNum() => _sessionCoordinator.CurrentSessionNum;

        // Session type checks
        public bool IsPracticeSession(int sessionNum) => _sessionCoordinator.IsPracticeSession(sessionNum);
        public bool IsQualifyingSession(int sessionNum) => _sessionCoordinator.IsQualifyingSession(sessionNum);
        public bool IsLoneQualifying(int sessionNum) => _sessionCoordinator.IsLoneQualifying(sessionNum);
        public bool IsRaceSession(int sessionNum) => _sessionCoordinator.IsRaceSession(sessionNum);

        // Current session type checks
        public bool IsCurrentSessionPractice() => IsPracticeSession(GetCurrentSessionNum());
        public bool IsCurrentSessionQualifying() => IsQualifyingSession(GetCurrentSessionNum());
        public bool IsCurrentSessionLoneQualifying() => IsLoneQualifying(GetCurrentSessionNum());
        public bool IsCurrentSessionRace() => IsRaceSession(GetCurrentSessionNum());

        // Live results access
        public List<LiveSessionData.ResultPosition> GetCurrentSessionResultsPositions() =>
            _sessionCoordinator.GetCurrentSessionResultsPositions();

        public List<LiveSessionData.FastestLapResult> GetCurrentSessionFastestLaps() =>
            _sessionCoordinator.GetCurrentSessionFastestLaps();

        // Helper methods for RelativeViewModel
        public bool ShouldUseFastestLapPositioning() => _sessionCoordinator.ShouldUseFastestLapPositioning();
        public bool ShouldHideRelativeDisplay() => _sessionCoordinator.ShouldHideRelativeDisplay();

        public List<(int carIdx, float fastestTime, int position)> GetFastestLapPositioning() =>
            _sessionCoordinator.GetFastestLapPositioning();

        // Legacy methods for backward compatibility
        public int[] GetQualifyResultsPositions() => _sessionCoordinator.GetQualifyResultsPositions();
        public float[] GetQualifyResultsFastestTimes() => _sessionCoordinator.GetQualifyResultsFastestTimes();
        public string GetCachedSessionYaml() => _sessionCoordinator.GetCachedSessionYaml();

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
                    _sessionCoordinator.ClearCache(); // Updated
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
            bool isPrimed = _isConnected && _sessionCoordinator.IsDataReady; // Updated
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
                    if (_sessionCoordinator.HasSessionDataChanged(sessionInfo)) // Updated
                    {
                        Console.WriteLine($"[SVappsLAB] New session data received, length: {sessionInfo.Length}");

                        if (_sessionCoordinator.ParseSessionData(sessionInfo)) // Updated
                        {
                            System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Session data parsed successfully");
                            SessionYamlAvailable?.Invoke(sessionInfo);
                            SessionDataUpdated?.Invoke();
                            CheckPrimedStateChange();

                            // Check if this represents a session transition for logging
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
        /// Check for session transitions and schedule logging accordingly
        /// This is called from OnSessionInfoUpdate when session data changes
        /// </summary>
        private void CheckForSessionTransitionLog()
        {
            try
            {
                // Use CurrentSessionNum from the coordinator
                int currentSessionNum = _sessionCoordinator.CurrentSessionNum;

                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] CheckForSessionTransitionLog: CurrentSessionNum={currentSessionNum}, LastLogged={_lastSessionNumForLog}");

                // Only log for valid sessions that we haven't logged yet
                if (currentSessionNum >= 0 && currentSessionNum <= 2 && currentSessionNum != _lastSessionNumForLog)
                {
                    string sessionType = _sessionCoordinator.GetSessionType(currentSessionNum);
                    string sessionName = _sessionCoordinator.GetSessionName(currentSessionNum);

                    // Get session duration from the coordinator
                    double sessionTimeSeconds = GetSessionTimeSeconds(currentSessionNum);

                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Scheduling session-aware log for {sessionName} ({sessionType}, SessionNum {currentSessionNum}, Duration: {sessionTimeSeconds}s)");

                    // Use session-aware logging with duration
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

        /// <summary>
        /// Get session duration from the coordinator
        /// </summary>
        private double GetSessionTimeSeconds(int sessionNum)
        {
            try
            {
                // Get actual session duration from parsed YAML data
                double sessionTimeSeconds = _sessionCoordinator.GetSessionTimeSeconds(sessionNum);

                if (sessionTimeSeconds > 0)
                {
                    return sessionTimeSeconds;
                }
                else
                {
                    // Fallback to sensible defaults if YAML parsing didn't get duration
                    string sessionType = _sessionCoordinator.GetSessionType(sessionNum);
                    System.Diagnostics.Debug.WriteLine($"[SVappsLAB] No session duration found, using fallback for {sessionType}");

                    return sessionType.ToLowerInvariant() switch
                    {
                        var type when type.Contains("practice") => 1200.0, // 20 minutes
                        var type when type.Contains("qualify") => 900.0,   // 15 minutes  
                        var type when type.Contains("race") => 1800.0,     // 30 minutes
                        _ => 900.0 // 15 minute fallback
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SVappsLAB] Error getting session time: {ex.Message}");
                return 900.0; // 15 minute fallback
            }
        }

        private void OnTelemetryUpdate(object sender, TelemetryData telemetryData)
        {
            try
            {
                var telemetryDict = _dataBuilder.BuildTelemetryDictionary(telemetryData);

                _latestSnapshot = new SVappsLABSnapshot(
                    telemetryDict,
                    _sessionCoordinator.GetCachedSessionYaml(), // Updated
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
                if (!_isRetryingYaml && _isConnected && !_sessionCoordinator.IsDataReady) // Updated
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
                if (!_isConnected || _sessionCoordinator.IsDataReady) // Updated
                {
                    StopYamlRetryTimer();
                    return;
                }

                Console.WriteLine("[SVappsLAB] Retrying YAML data retrieval...");
                string sessionInfo = _client?.GetRawTelemetrySessionInfoYaml();

                if (!string.IsNullOrEmpty(sessionInfo))
                {
                    Console.WriteLine($"[SVappsLAB] Retry successful - got session data, length: {sessionInfo.Length}");

                    if (_sessionCoordinator.ParseSessionData(sessionInfo)) // Updated
                    {
                        Console.WriteLine("[SVappsLAB] Retry parse successful");
                        SessionYamlAvailable?.Invoke(sessionInfo);
                        SessionDataUpdated?.Invoke();
                        CheckPrimedStateChange();

                        // Check for session dump after successful retry
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
                _sessionCoordinator.ClearCache(); // Updated

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