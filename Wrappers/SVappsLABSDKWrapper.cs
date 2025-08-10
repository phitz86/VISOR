using VISOR.Interfaces;
using Microsoft.Extensions.Logging;
using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VISOR.Wrappers
{
    public class NullLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) { }
    }

    public class SVappsLABSDKWrapper : IracingTelemetryWrapper
    {
        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private Dictionary<string, object> _latestData = new();
        private string _latestSessionInfo = string.Empty;
        private readonly object _dataLock = new();
        private readonly Stopwatch _pollTimer = new();
        private bool _isConnected = false;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;

        // YAML-parsed session data cache
        private int[] _cachedCarNumbers = new int[64]; // iRacing supports max 64 cars
        private string[] _cachedCarClasses = new string[64]; // Car classes from YAML
        private string _lastSessionDataHash = string.Empty;

        // Required fields from AssemblyInfo.cs
        private static readonly HashSet<string> _requiredTelemetryFields = new()
        {
            "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
            "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
            "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
            "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
            "CarIdxLap", "CarIdxLastLapTime",
            "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
            "SessionLapsTotal", "SessionNum",
            "TrackTemp", "AirTemp", "Skies", "WindVel"
        };

        // Fields that must come from YAML parsing
        private static readonly HashSet<string> _yamlOnlyFields = new()
        {
            "CarIdxCarNumber", "CarIdxClass"
        };

        private static readonly PerformanceCounter _cpuCounter = InitializeCpuCounter();

        public string Name => "SVappsLAB iRacingTelemetrySDK";
        public bool IsConnected => _isConnected;

        public SVappsLABSDKWrapper()
        {
            _logger = new NullLogger<SVappsLABSDKWrapper>();
            // Initialize car classes array
            for (int i = 0; i < _cachedCarClasses.Length; i++)
            {
                _cachedCarClasses[i] = string.Empty;
            }
        }

        private static PerformanceCounter InitializeCpuCounter()
        {
            try
            {
                return new PerformanceCounter("Process", "% Processor Time", Process.GetCurrentProcess().ProcessName);
            }
            catch { return null; }
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
                Console.WriteLine($"[SVappsLAB] Tracking {_requiredTelemetryFields.Count} telemetry fields + {_yamlOnlyFields.Count} YAML fields");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Initialize error: {ex.Message}");
                return false;
            }
        }

        bool IracingTelemetryWrapper.Initialize()
        {
            return Initialize().GetAwaiter().GetResult();
        }

        public HashSet<string> GetSupportedFields()
        {
            var allFields = new HashSet<string>(_requiredTelemetryFields);
            foreach (var field in _yamlOnlyFields)
            {
                allFields.Add(field);
            }
            return allFields;
        }

        public Dictionary<string, Type> GetFieldTypes()
        {
            var fieldTypes = new Dictionary<string, Type>
            {
                // Lap timing
                ["LapCurrentLapTime"] = typeof(float),
                ["LapLastLapTime"] = typeof(float),
                ["LapBestLapTime"] = typeof(float),
                ["LapDeltaToBestLap"] = typeof(float),
                ["LapDeltaToOptimalLap"] = typeof(float),
                ["LapDeltaToSessionBestLap"] = typeof(float),
                ["Lap"] = typeof(int),

                // Fuel and car state
                ["FuelLevel"] = typeof(float),
                ["FuelUsePerHour"] = typeof(float),
                ["Gear"] = typeof(int),
                ["Speed"] = typeof(float),
                ["RPM"] = typeof(float),

                // Positioning arrays
                ["CarIdxLapDistPct"] = typeof(float[]),
                ["CarIdxPosition"] = typeof(int[]),
                ["CarIdxClassPosition"] = typeof(int[]),
                ["CarIdxTrackSurface"] = typeof(int[]),
                ["CarIdxLap"] = typeof(int[]),
                ["CarIdxLastLapTime"] = typeof(float[]),

                // Session info
                ["SessionState"] = typeof(int),
                ["SessionTime"] = typeof(double),
                ["SessionTimeRemain"] = typeof(double),
                ["SessionLapsRemain"] = typeof(int),
                ["SessionLapsTotal"] = typeof(int),
                ["SessionNum"] = typeof(int),

                // Environmental
                ["TrackTemp"] = typeof(float),
                ["AirTemp"] = typeof(float),
                ["Skies"] = typeof(int),
                ["WindVel"] = typeof(float),

                // YAML-only fields
                ["CarIdxCarNumber"] = typeof(int[]),
                ["CarIdxClass"] = typeof(string[])
            };

            return fieldTypes;
        }

        public bool SupportsField(string fieldName)
        {
            return _requiredTelemetryFields.Contains(fieldName) || _yamlOnlyFields.Contains(fieldName);
        }

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
                    // Parse YAML data immediately when session info updates
                    ParseYamlSessionData(sessionInfo);
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

        private int _printCounter = 0;

        private void ParseYamlSessionData(string sessionData)
        {
            try
            {
                // Simple hash check to avoid re-parsing identical session data
                var currentHash = sessionData.GetHashCode().ToString();
                if (currentHash == _lastSessionDataHash) return;

                // Reset the arrays
                Array.Fill(_cachedCarNumbers, 0);
                Array.Fill(_cachedCarClasses, string.Empty);

                // Parse car data from YAML using simple string parsing
                // Look for patterns like: "- CarIdx: 0" followed by "CarNumber: "42"" and "CarClassShortName: "GT3""
                var lines = sessionData.Split('\n');
                int currentCarIdx = -1;

                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();

                    // Look for CarIdx entries
                    if (line.StartsWith("- CarIdx:"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out currentCarIdx))
                        {
                            // Valid CarIdx found, continue to next lines to find CarNumber and CarClass
                            continue;
                        }
                    }

                    // Look for CarNumber entries if we have a valid CarIdx
                    if (currentCarIdx >= 0 && currentCarIdx < 64 && line.StartsWith("CarNumber:"))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length >= 2)
                        {
                            var carNumberStr = parts[1].Trim().Trim('"'); // Remove quotes
                            if (int.TryParse(carNumberStr, out int carNumber))
                            {
                                _cachedCarNumbers[currentCarIdx] = carNumber;
                            }
                        }
                    }

                    // Look for CarClassShortName entries if we have a valid CarIdx
                    if (currentCarIdx >= 0 && currentCarIdx < 64 && line.StartsWith("CarClassShortName:"))
                    {
                        var parts = line.Split(':', 2);
                        if (parts.Length >= 2)
                        {
                            var carClass = parts[1].Trim().Trim('"'); // Remove quotes
                            _cachedCarClasses[currentCarIdx] = carClass;
                        }
                        // Reset CarIdx after processing both CarNumber and CarClass
                        currentCarIdx = -1;
                    }
                }

                _lastSessionDataHash = currentHash;
                Console.WriteLine($"[SVappsLAB] Parsed session data: Updated car numbers and classes");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Error parsing YAML session data: {ex.Message}");
            }
        }

        private void OnTelemetryUpdate(object sender, TelemetryData telemetryData)
        {
            try
            {
                var snapshot = BuildDirectTelemetryDict(telemetryData);

                lock (_dataLock)
                {
                    _latestData = snapshot;
                }

                // Log every 100th sample (approx 1.6s at 60Hz)
                if (++_printCounter % 100 == 0)
                {
                    Console.WriteLine($"[SVappsLAB] Extracted {snapshot.Count} telemetry fields (every 100th sample)");

                    // Enhanced array debugging
                    if (snapshot.TryGetValue("CarIdxLapDistPct", out var dist) && dist is float[] distArr)
                    {
                        Console.WriteLine($"[SVappsLAB] CarIdxLapDistPct[0]: {distArr[0]} (Len={distArr.Length})");
                    }

                    if (snapshot.TryGetValue("CarIdxPosition", out var pos) && pos is int[] posArr)
                    {
                        Console.WriteLine($"[SVappsLAB] CarIdxPosition[0]: {posArr[0]} (Len={posArr.Length})");
                    }

                    if (snapshot.TryGetValue("CarIdxCarNumber", out var carNum) && carNum is int[] carNumArr)
                    {
                        Console.WriteLine($"[SVappsLAB] CarIdxCarNumber[0]: {carNumArr[0]} (Len={carNumArr.Length})");
                    }

                    if (snapshot.TryGetValue("CarIdxClass", out var carClass) && carClass is string[] carClassArr)
                    {
                        Console.WriteLine($"[SVappsLAB] CarIdxClass[0]: '{carClassArr[0]}' (Len={carClassArr.Length})");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Telemetry update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Build telemetry dictionary using direct typed access to TelemetryData struct - NO REFLECTION
        /// </summary>
        private Dictionary<string, object> BuildDirectTelemetryDict(TelemetryData telemetryData)
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

                // Add YAML-parsed data
                dict["CarIdxCarNumber"] = (int[])_cachedCarNumbers.Clone();
                dict["CarIdxClass"] = (string[])_cachedCarClasses.Clone();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Error building direct telemetry dictionary: {ex.Message}");
            }

            return dict;
        }

        public TelemetrySnapshot GetSnapshot()
        {
            _pollTimer.Restart();

            Dictionary<string, object> dataSnapshot;
            string sessionSnapshot;

            lock (_dataLock)
            {
                dataSnapshot = new Dictionary<string, object>(_latestData);
                sessionSnapshot = _latestSessionInfo;
            }

            _pollTimer.Stop();
            LastPollDuration = _pollTimer.Elapsed;

            // Optional: Log performance metrics
            if (dataSnapshot.Count > 0)
            {
                WrapperPerformanceMonitor.LogSnapshot(Name, dataSnapshot.Count, _pollTimer.ElapsedMilliseconds);
            }

            return new SVappsLABSnapshot(dataSnapshot, sessionSnapshot, DateTime.UtcNow);
        }

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
                Array.Fill(_cachedCarNumbers, 0);
                Array.Fill(_cachedCarClasses, string.Empty);
                _lastSessionDataHash = string.Empty;

                Console.WriteLine("[SVappsLAB] Shutdown complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SVappsLAB] Shutdown error: {ex.Message}");
            }
        }

        public TimeSpan LastPollDuration { get; private set; }
        public long MemoryUsageBytes => Process.GetCurrentProcess().PrivateMemorySize64;
        public double CpuUsagePercent => _cpuCounter?.NextValue() ?? 0.0;

        public void Dispose()
        {
            Shutdown();
            _cpuCounter?.Dispose();
        }
    }

    /// <summary>
    /// Optional performance monitoring helper
    /// </summary>
    public static class WrapperPerformanceMonitor
    {
        public static void LogSnapshot(string wrapperName, int fieldCount, long durationMs)
        {
            // Log performance metrics if needed
            // This can be expanded to track averages, peaks, etc.
            if (durationMs > 5) // Only log if polling took longer than 5ms
            {
                Console.WriteLine($"[{wrapperName}] Performance: {fieldCount} fields in {durationMs}ms");
            }
        }
    }
}