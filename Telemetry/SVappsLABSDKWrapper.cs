using Microsoft.Extensions.Logging;
using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VISOR.Telemetry
{
    [RequiredTelemetryVars([
        "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
        "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
        "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
        "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
        "CarIdxLap", "CarIdxLastLapTime",
        "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
        "SessionLapsTotal", "SessionNum", "PlayerCarIdx"
    ])]
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields
        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly TelemetryDataBuilder _dataBuilder;
        private readonly SessionDataParser _sessionParser;
        private SVappsLABSnapshot _latestSnapshot;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;
        private bool _isConnected = false;

        // YAML retry logic
        private System.Timers.Timer _yamlRetryTimer;
        private readonly object _retryLock = new();
        private bool _isRetryingYaml = false;
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
                            Console.WriteLine($"[SVappsLAB] Session data parsed successfully");
                            SessionYamlAvailable?.Invoke(sessionInfo);
                            SessionDataUpdated?.Invoke();
                            CheckPrimedStateChange();
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