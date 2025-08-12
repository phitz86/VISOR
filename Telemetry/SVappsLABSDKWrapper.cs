using Microsoft.Extensions.Logging;
using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VISOR.Telemetry
{
    // Null logger implementation for cases where logging is not required
    public class NullLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
    }

    // Wrapper for SVappsLAB iRacing Telemetry SDK with clean separation of concerns
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields

        private ITelemetryClient<SVappsLAB.iRacingTelemetrySDK.TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly SessionDataParser _sessionParser;

        private Dictionary<string, object> _latestData = new();
        private string _latestSessionInfo = string.Empty;
        private readonly object _dataLock = new();

        private bool _isConnected = false;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;

        #endregion

        #region Public Properties

        public string Name => "SVappsLAB iRacingTelemetrySDK";
        public bool IsConnected => _isConnected;

        #endregion

        #region Events

        // Fired when a new telemetry snapshot is available
        public event Action<SVappsLABSnapshot> SnapshotAvailable;

        // Fired when session YAML data is available
        public event Action<string> SessionYamlAvailable;

        #endregion

        #region Constructor

        public SVappsLABSDKWrapper()
        {
            _logger = new NullLogger<SVappsLABSDKWrapper>();
            _sessionParser = new SessionDataParser();
        }

        #endregion

        #region Public Methods

        // Initialize the SDK wrapper asynchronously
        public async Task<bool> Initialize()
        {
            try
            {
                Console.WriteLine("[SVappsLAB] Starting initialization...");

                _client = TelemetryClient<SVappsLAB.iRacingTelemetrySDK.TelemetryData>.Create(_logger);
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
        /// Start the telemetry monitoring
        /// </summary>
        public async Task<bool> Start()
        {
            return await Initialize();
        }

        /// <summary>
        /// Start the telemetry monitoring synchronously
        /// </summary>
        public bool StartSync()
        {
            return InitializeSync();
        }

        /// <summary>
        /// Get all supported field names
        /// </summary>
        public HashSet<string> GetSupportedFields()
        {
            return TelemetryFieldRegistry.GetAllSupportedFields();
        }

        /// <summary>
        /// Get field type mappings for all supported fields
        /// </summary>
        public Dictionary<string, Type> GetFieldTypes()
        {
            return new Dictionary<string, Type>(TelemetryFieldRegistry.FieldTypes);
        }

        /// <summary>
        /// Check if a field is supported by this wrapper
        /// </summary>
        public bool SupportsField(string fieldName)
        {
            return TelemetryFieldRegistry.IsFieldSupported(fieldName);
        }

        /// <summary>
        /// Get a snapshot of current telemetry data
        /// </summary>
        public SVappsLABSnapshot GetSnapshot()
        {
            Dictionary<string, object> dataSnapshot;
            string sessionSnapshot;

            lock (_dataLock)
            {
                dataSnapshot = new Dictionary<string, object>(_latestData);
                sessionSnapshot = _latestSessionInfo;
            }

            return new SVappsLABSnapshot(dataSnapshot, sessionSnapshot, DateTime.UtcNow);
        }

        /// <summary>
        /// Dump the latest YAML session data to a file
        /// </summary>
        public string DumpLatestYaml()
        {
            return _sessionParser.DumpSessionData(_latestSessionInfo);
        }

        /// <summary>
        /// Shutdown the SDK wrapper and clean up resources
        /// </summary>
        public void Shutdown()
        {
            try
            {
                Console.WriteLine("[SVappsLAB] Starting shutdown...");
                _isConnected = false;

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

        #endregion

        #region Event Handlers

        private void OnConnectStateChanged(object sender, EventArgs e)
        {
            bool newConnectionState = _client != null && _client.IsConnected();
            if (newConnectionState != _isConnected)
            {
                _isConnected = newConnectionState;
                Console.WriteLine($"[SVappsLAB] Connection state changed: {_isConnected}");
            }
        }

        private void OnSessionInfoUpdate(object sender, object e)
        {
            try
            {
                string sessionInfo = string.Empty;
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
                        sessionInfo = rawProperty?.GetValue(sessionResult)?.ToString() ?? sessionResult.ToString();
                    }
                }

                if (!string.IsNullOrEmpty(sessionInfo))
                {
                    // Parse YAML data using our dedicated parser
                    _sessionParser.ParseSessionData(sessionInfo);

                    // Fire session YAML event
                    SessionYamlAvailable?.Invoke(sessionInfo);
                }

                lock (_dataLock)
                {
                    _latestSessionInfo = sessionInfo;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Session info error: {ex.Message}");
            }
        }

        private void OnTelemetryUpdate(object sender, SVappsLAB.iRacingTelemetrySDK.TelemetryData telemetryData)
        {
            try
            {
                var snapshot = BuildTelemetryDictionary(telemetryData);

                lock (_dataLock)
                {
                    _latestData = snapshot;
                }

                // Fire snapshot available event
                var snapshotObj = new SVappsLABSnapshot(snapshot, _latestSessionInfo, DateTime.UtcNow);
                SnapshotAvailable?.Invoke(snapshotObj);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Telemetry update error: {ex.Message}");
            }
        }

        #endregion

        #region Private Methods

        // Build telemetry dictionary using direct typed access to TelemetryData struct
        private Dictionary<string, object> BuildTelemetryDictionary(SVappsLAB.iRacingTelemetrySDK.TelemetryData telemetryData)
        {
            var dict = new Dictionary<string, object>();

            try
            {
                // Direct access to telemetry fields - no reflection needed

                // Lap timing
                dict["LapCurrentLapTime"] = telemetryData.LapCurrentLapTime;
                dict["LapLastLapTime"] = telemetryData.LapLastLapTime;
                dict["LapBestLapTime"] = telemetryData.LapBestLapTime;
                dict["LapDeltaToBestLap"] = telemetryData.LapDeltaToBestLap;
                dict["LapDeltaToOptimalLap"] = telemetryData.LapDeltaToOptimalLap;
                dict["LapDeltaToSessionBestLap"] = telemetryData.LapDeltaToSessionBestLap;
                dict["Lap"] = telemetryData.Lap;

                // Fuel and car state
                dict["FuelLevel"] = telemetryData.FuelLevel;
                dict["FuelUsePerHour"] = telemetryData.FuelUsePerHour;
                dict["Gear"] = telemetryData.Gear;
                dict["Speed"] = telemetryData.Speed;
                dict["RPM"] = telemetryData.RPM;

                // Positioning arrays - direct typed access
                dict["CarIdxLapDistPct"] = telemetryData.CarIdxLapDistPct;
                dict["CarIdxPosition"] = telemetryData.CarIdxPosition;
                dict["CarIdxClassPosition"] = telemetryData.CarIdxClassPosition;
                dict["CarIdxTrackSurface"] = telemetryData.CarIdxTrackSurface;
                dict["CarIdxLap"] = telemetryData.CarIdxLap;
                dict["CarIdxLastLapTime"] = telemetryData.CarIdxLastLapTime;

                // Session info
                dict["SessionState"] = telemetryData.SessionState;
                dict["SessionTime"] = telemetryData.SessionTime;
                dict["SessionTimeRemain"] = telemetryData.SessionTimeRemain;
                dict["SessionLapsRemain"] = telemetryData.SessionLapsRemain;
                dict["SessionLapsTotal"] = telemetryData.SessionLapsTotal;
                dict["SessionNum"] = telemetryData.SessionNum;

                // Environmental
                dict["TrackTemp"] = telemetryData.TrackTemp;
                dict["AirTemp"] = telemetryData.AirTemp;
                dict["Skies"] = telemetryData.Skies;
                dict["WindVel"] = telemetryData.WindVel;

                // Add YAML-parsed data from our session parser
                dict["CarIdxCarNumber"] = _sessionParser.CarNumbers;
                dict["CarIdxClass"] = _sessionParser.CarClasses;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Error building telemetry dictionary: {ex.Message}");
            }

            return dict;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            Shutdown();
        }

        #endregion
    }
}