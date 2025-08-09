using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GRiD.Interfaces;
using iRacingSDK;

namespace GRiD.Wrappers
{
    public class IRacingSDKNetWrapper : IracingTelemetryWrapper
    {
        private iRacingConnection _iracing;
        private volatile bool _isConnected;
        private volatile bool _isDisposed;
        private readonly object _lockObject = new();
        private CancellationTokenSource _cancellationTokenSource;
        private Task _backgroundPollingTask;
        private DataSample _latestSample;

        private readonly Stopwatch _pollTimer = new();
        private static readonly PerformanceCounter _cpuCounter = InitializeCpuCounter();

        // Field discovery cache with enhanced retry mechanism
        private HashSet<string> _supportedFields = new();
        private Dictionary<string, Type> _fieldTypes = new();
        private bool _fieldsDiscovered = false;
        private readonly HashSet<string> _loggedMissingFields = new();
        private int _discoveryAttempts = 0;
        private const int MAX_DISCOVERY_ATTEMPTS = 10; // Increased from 5
        private DateTime _lastDiscoveryAttempt = DateTime.MinValue;
        private readonly TimeSpan DISCOVERY_RETRY_INTERVAL = TimeSpan.FromSeconds(2); // Increased from 1s

        // Car number caching
        private int[] _cachedCarNumbers = new int[64]; // iRacing supports max 64 cars
        private string _lastSessionDataHash = string.Empty;

        // Same priority fields for comparison
        private static readonly string[] OverlayPriorityFields = new[]
        {
            "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
            "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
            "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
            "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
            "DriverCarIdx", "CarIdxLap", "CarIdxLastLapTime",
            "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
            "SessionLapsTotal", "SessionNum", "SessionType", "SessionTick",
            "TrackTemp", "AirTemp", "Skies", "WindVel",
            "Latency", "FramesPerSecond"
        };

        public string Name => "iRacingSDK.Net";
        public bool IsConnected => _isConnected && _iracing?.IsConnected == true;

        public TimeSpan LastPollDuration { get; private set; }
        public long MemoryUsageBytes => Process.GetCurrentProcess().PrivateMemorySize64;
        public double CpuUsagePercent => _cpuCounter?.NextValue() ?? 0.0;

        private static PerformanceCounter InitializeCpuCounter()
        {
            try
            {
                return new PerformanceCounter("Process", "% Processor Time", Process.GetCurrentProcess().ProcessName);
            }
            catch { return null; }
        }

        public bool Initialize()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_isDisposed) return false;

                    Console.WriteLine($"[{Name}] Starting initialization...");

                    _cancellationTokenSource = new CancellationTokenSource();
                    _iracing = new iRacingConnection();

                    _backgroundPollingTask = Task.Run(() =>
                    {
                        try
                        {
                            foreach (var sample in _iracing.GetDataFeed())
                            {
                                if (_cancellationTokenSource.IsCancellationRequested) break;

                                lock (_lockObject)
                                {
                                    _latestSample = sample;
                                    if (_iracing.IsConnected && !_isConnected)
                                    {
                                        _isConnected = true;
                                        Console.WriteLine($"[{Name}] Connection established.");
                                    }

                                    // Attempt field discovery when we have telemetry data
                                    TryDiscoverFields();
                                }

                                Thread.Sleep(16); // ~60Hz
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{Name}] Background polling error: {ex.Message}");
                        }
                    });

                    // Wait for connection
                    const int maxWaitMs = 3000;
                    int waited = 0;
                    while (!_iracing.IsConnected && waited < maxWaitMs)
                    {
                        Thread.Sleep(100);
                        waited += 100;
                    }

                    if (_iracing.IsConnected)
                    {
                        Console.WriteLine($"[{Name}] Connected after {waited}ms");
                        _isConnected = true;
                        // Don't force discovery here - let it happen naturally when telemetry data is available
                        return true;
                    }

                    Console.WriteLine($"[{Name}] Not connected after {maxWaitMs}ms");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Initialize error: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private void TryDiscoverFields()
        {
            // Don't retry if already discovered or too many attempts
            if (_fieldsDiscovered || _discoveryAttempts >= MAX_DISCOVERY_ATTEMPTS) return;

            // Don't retry too frequently
            if (DateTime.UtcNow - _lastDiscoveryAttempt < DISCOVERY_RETRY_INTERVAL) return;

            // Must have telemetry data available
            if (_latestSample?.Telemetry == null) return;

            _lastDiscoveryAttempt = DateTime.UtcNow;
            _discoveryAttempts++;

            try
            {
                Console.WriteLine($"[{Name}] Attempting field discovery (attempt {_discoveryAttempts}/{MAX_DISCOVERY_ATTEMPTS})...");

                _supportedFields.Clear();
                _fieldTypes.Clear();

                // Use reflection to examine the telemetry object
                var telemetryType = _latestSample.Telemetry.GetType();
                var properties = telemetryType.GetProperties();

                if (properties.Length == 0)
                {
                    Console.WriteLine($"[{Name}] No properties found in telemetry object - telemetry data may not be ready yet");
                    return;
                }

                // Additional validation: try to access a few known properties to ensure they're not null/default
                int validPropertyCount = 0;
                foreach (var property in properties)
                {
                    try
                    {
                        var value = property.GetValue(_latestSample.Telemetry);
                        _supportedFields.Add(property.Name);
                        _fieldTypes[property.Name] = property.PropertyType;

                        // Count non-null/non-default values to validate telemetry quality
                        if (value != null && !IsDefaultValue(value, property.PropertyType))
                        {
                            validPropertyCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{Name}] Warning: Could not access property '{property.Name}': {ex.Message}");
                    }
                }

                // Require at least some valid data before considering discovery successful
                if (validPropertyCount < 5)
                {
                    Console.WriteLine($"[{Name}] Discovery attempt {_discoveryAttempts}: Only found {validPropertyCount} valid properties, retrying...");
                    _supportedFields.Clear();
                    _fieldTypes.Clear();
                    return;
                }

                // Log priority field availability
                var availablePriorityFields = OverlayPriorityFields.Where(f => _supportedFields.Contains(f)).ToList();
                var missingPriorityFields = OverlayPriorityFields.Where(f => !_supportedFields.Contains(f)).ToList();

                Console.WriteLine($"[{Name}] Field Discovery Complete:");
                Console.WriteLine($"  Total fields available: {_supportedFields.Count}");
                Console.WriteLine($"  Valid properties with data: {validPropertyCount}");
                Console.WriteLine($"  Priority fields available: {availablePriorityFields.Count}/{OverlayPriorityFields.Length}");

                if (missingPriorityFields.Any())
                {
                    Console.WriteLine($"  Missing priority fields: {string.Join(", ", missingPriorityFields)}");
                }

                _fieldsDiscovered = true;
                Console.WriteLine($"[{Name}] Field discovery successful on attempt {_discoveryAttempts}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Field discovery attempt {_discoveryAttempts} failed: {ex.Message}");

                if (_discoveryAttempts >= MAX_DISCOVERY_ATTEMPTS)
                {
                    Console.WriteLine($"[{Name}] Maximum discovery attempts reached. Field discovery failed permanently.");
                }
            }
        }

        private bool IsDefaultValue(object value, Type type)
        {
            if (value == null) return true;

            // Check for default values of common types
            return type.IsValueType && value.Equals(Activator.CreateInstance(type));
        }

        public HashSet<string> GetSupportedFields()
        {
            if (!_fieldsDiscovered && IsConnected) TryDiscoverFields();
            return new HashSet<string>(_supportedFields);
        }

        public Dictionary<string, Type> GetFieldTypes()
        {
            if (!_fieldsDiscovered && IsConnected) TryDiscoverFields();
            return new Dictionary<string, Type>(_fieldTypes);
        }

        public bool SupportsField(string fieldName)
        {
            if (!_fieldsDiscovered && IsConnected) TryDiscoverFields();
            return _supportedFields.Contains(fieldName);
        }

        public TelemetrySnapshot GetSnapshot()
        {
            if (!IsConnected || _isDisposed)
            {
                return new TelemetrySnapshot
                {
                    IsValid = false,
                    Timestamp = DateTime.UtcNow,
                    SessionTick = 0,
                    RawTelemetryData = new Dictionary<string, object>(),
                    RawSessionData = string.Empty
                };
            }

            try
            {
                lock (_lockObject)
                {
                    _pollTimer.Restart();

                    var sample = _latestSample;
                    if (sample?.Telemetry == null)
                    {
                        _pollTimer.Stop();
                        LastPollDuration = _pollTimer.Elapsed;
                        Console.WriteLine($"[{Name}] GetSnapshot: sample or sample.Telemetry is null");
                        return new TelemetrySnapshot
                        {
                            IsValid = false,
                            Timestamp = DateTime.UtcNow,
                            SessionTick = 0,
                            RawTelemetryData = new Dictionary<string, object>(),
                            RawSessionData = string.Empty
                        };
                    }

                    // Debug output for telemetry sample validation
                    var telemetryType = sample.Telemetry.GetType();
                    var sampleProperties = telemetryType.GetProperties();

                    if (sampleProperties.Length == 0)
                    {
                        Console.WriteLine($"[{Name}] GetSnapshot: Telemetry object has no properties");
                    }

                    // Trigger field discovery if needed and we have telemetry data
                    TryDiscoverFields();

                    var telemetryData = BuildAdaptiveTelemetryDict(sample.Telemetry, sample.SessionData?.Raw ?? string.Empty);
                    var timestamp = DateTime.UtcNow;
                    string sessionData = string.Empty;
                    int sessionTick = 0;

                    try
                    {
                        sessionData = sample.SessionData?.Raw ?? string.Empty;
                        if (telemetryData.ContainsKey("SessionTick"))
                        {
                            sessionTick = Convert.ToInt32(telemetryData["SessionTick"]);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{Name}] Warning: Error extracting session data: {ex.Message}");
                    }

                    _pollTimer.Stop();
                    LastPollDuration = _pollTimer.Elapsed;

                    return new TelemetrySnapshot
                    {
                        IsValid = telemetryData.Count > 0, // Only valid if we got some data
                        Timestamp = timestamp,
                        SessionTick = sessionTick,
                        RawTelemetryData = telemetryData,
                        RawSessionData = sessionData
                    };
                }
            }
            catch (Exception ex)
            {
                _pollTimer.Stop();
                LastPollDuration = _pollTimer.Elapsed;
                Console.WriteLine($"[{Name}] Error retrieving telemetry: {ex.Message}");
                Console.WriteLine($"[{Name}] Stack trace: {ex.StackTrace}");
                return new TelemetrySnapshot
                {
                    IsValid = false,
                    Timestamp = DateTime.UtcNow,
                    SessionTick = 0,
                    RawTelemetryData = new Dictionary<string, object>(),
                    RawSessionData = string.Empty
                };
            }
        }

        private int _telemetryLogCounter = 0;

        private void UpdateCarNumbersCache(string sessionData)
        {
            try
            {
                // Simple hash check to avoid re-parsing identical session data
                var currentHash = sessionData.GetHashCode().ToString();
                if (currentHash == _lastSessionDataHash) return;

                // Reset the array
                Array.Fill(_cachedCarNumbers, 0);

                // Parse car numbers from YAML using simple string parsing
                // Look for patterns like: "- CarIdx: 0" followed by "CarNumber: "42""
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
                            // Valid CarIdx found, continue to next lines to find CarNumber
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
                        currentCarIdx = -1; // Reset to avoid re-using
                    }
                }

                _lastSessionDataHash = currentHash;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Error parsing car numbers from SessionData: {ex.Message}");
            }
        }
        private Dictionary<string, object> BuildAdaptiveTelemetryDict(dynamic telemetry, string sessiondata)
        {
            var dict = new Dictionary<string, object>();

            try
            {
                if (telemetry == null)
                {
                    Console.WriteLine($"[{Name}] BuildAdaptiveTelemetryDict: telemetry parameter is null");
                    return dict;
                }

                // Debug: Verify telemetry object structure
                var telemetryType = telemetry.GetType();
                var allProperties = telemetryType.GetProperties();

                if (allProperties.Length == 0)
                {
                    Console.WriteLine($"[{Name}] BuildAdaptiveTelemetryDict: telemetry object has no properties");
                    return dict;
                }

                // Only request fields we know are supported, or all priority fields if discovery incomplete
                var fieldsToRequest = _fieldsDiscovered
                    ? OverlayPriorityFields.Where(f => _supportedFields.Contains(f))
                    : OverlayPriorityFields; // Try all priority fields if discovery hasn't completed yet

                int successCount = 0;
                int failureCount = 0;
                int nullCount = 0;

                foreach (var fieldName in fieldsToRequest)
                {
                    try
                    {
                        var property = telemetry.GetType().GetProperty(fieldName);
                        if (property != null)
                        {
                            var value = property.GetValue(telemetry);

                            // Normalize arrays for JSON serialization consistency
                            if (value is Array arr)
                            {
                                dict[fieldName] = arr.Cast<object>().ToArray(); // safe for JSON serialization
                            }
                            else
                            {
                                dict[fieldName] = value;
                            }

                            if (value != null)
                                successCount++;
                            else
                                nullCount++;
                        }
                        else if (!_fieldsDiscovered && !_loggedMissingFields.Contains(fieldName))
                        {
                            // Only log missing fields once and only if discovery hasn't completed
                            failureCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        if (!_loggedMissingFields.Contains(fieldName) && _loggedMissingFields.Count < 10)
                        {
                            Console.WriteLine($"[{Name}] Error reading field '{fieldName}': {ex.Message}");
                            _loggedMissingFields.Add(fieldName);
                        }
                        dict[fieldName] = null;
                    }
                }

                // Enhanced logging every 100th sample
                if (++_telemetryLogCounter % 100 == 0)
                {
                    Console.WriteLine($"[{Name}] Telemetry extraction: {successCount} successful, {nullCount} null, {failureCount} failed, {dict.Count} total fields");

                    // Log sample values for key fields
                    var sampleFields = new[] { "SessionTick", "SessionTime", "SessionState", "Speed", "RPM", "Lap" };
                    var samples = sampleFields.Where(f => dict.ContainsKey(f))
                                            .Select(f => $"{f}={dict[f]}")
                                            .Take(4);
                    if (samples.Any())
                    {
                        Console.WriteLine($"[{Name}] Sample values: {string.Join(", ", samples)}");
                    }
                    else
                    {
                        Console.WriteLine($"[{Name}] WARNING: No sample field values found in telemetry data");
                    }

                    // Enhanced CarIdx array debugging
                    if (dict.TryGetValue("CarIdxLapDistPct", out var dist) && dist is Array distArr)
                    {
                        Console.WriteLine($"[{Name}] CarIdxLapDistPct[0]: {distArr.GetValue(0)} (Len={distArr.Length})");
                    }

                    if (dict.TryGetValue("CarIdxPosition", out var pos) && pos is Array posArr)
                    {
                        Console.WriteLine($"[{Name}] CarIdxPosition[0]: {posArr.GetValue(0)} (Len={posArr.Length})");
                    }

                    if (dict.TryGetValue("CarIdxClassPosition", out var classPos) && classPos is Array classPosArr)
                    {
                        Console.WriteLine($"[{Name}] CarIdxClassPosition[0]: {classPosArr.GetValue(0)} (Len={classPosArr.Length})");
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Error building adaptive telemetry dictionary: {ex.Message}");
                Console.WriteLine($"[{Name}] Stack trace: {ex.StackTrace}");
            }

            return dict;
        }

        public void Shutdown()
        {
            try
            {
                lock (_lockObject)
                {
                    if (_isDisposed) return;

                    Console.WriteLine($"[{Name}] Starting shutdown...");
                    _isConnected = false;
                    _cancellationTokenSource?.Cancel();

                    _iracing = null;
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;

                    _backgroundPollingTask = null;
                    _latestSample = null;
                    _fieldsDiscovered = false;
                    _supportedFields.Clear();
                    _fieldTypes.Clear();
                    _loggedMissingFields.Clear();
                    _discoveryAttempts = 0;
                    _lastDiscoveryAttempt = DateTime.MinValue;
                    _isDisposed = true;

                    Console.WriteLine($"[{Name}] Shutdown complete");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Name}] Shutdown error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Shutdown();
            _cpuCounter?.Dispose();
        }

        ~IRacingSDKNetWrapper()
        {
            Dispose();
        }
    }
}