using GRiD.Interfaces;
using IRSDKSharper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace GRiD.Wrappers
{
    public class IRSDKSharperWrapper : IracingTelemetryWrapper
    {
        private IRacingSdk _sdk;
        private readonly Stopwatch _pollTimer = new();
        private static readonly PerformanceCounter _cpuCounter = InitializeCpuCounter();
        private bool _isInitialized = false;

        // Field discovery cache
        private HashSet<string> _supportedFields = new();
        private Dictionary<string, Type> _fieldTypes = new();
        private bool _fieldsDiscovered = false;
        private readonly HashSet<string> _loggedMissingFields = new();

        // Car number caching
        private int[] _cachedCarNumbers = new int[64]; // iRacing supports max 64 cars
        private string _lastSessionDataHash = string.Empty;

        // Priority fields for overlay functionality
        private static readonly string[] OverlayPriorityFields = new[]
        {
            // Lap timing
            "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
            "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
            
            // Fuel and car state
            "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
            
            // Positioning and relative data
            "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
            "DriverCarIdx", "CarIdxLap", "CarIdxLastLapTime",
            
            // Session info
            "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
            "SessionLapsTotal", "SessionNum", "SessionType", "SessionTick",
            
            // Environmental
            "TrackTemp", "AirTemp", "Skies", "WindVel",
            
            // Performance monitoring
            "Latency", "FramesPerSecond"
        };

        public string Name => "IRSDKSharper";
        public bool IsConnected => _sdk?.IsConnected == true;

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
                Console.WriteLine("[IRSDKSharper] Starting initialization...");

                _sdk = new IRacingSdk();
                _sdk.OnConnected += () =>
                {
                    Console.WriteLine("[IRSDKSharper] Connected to iRacing!");
                    // Field discovery will happen later when telemetry data is available
                };
                _sdk.OnDisconnected += () =>
                {
                    Console.WriteLine("[IRSDKSharper] Disconnected from iRacing");
                    _fieldsDiscovered = false;
                    _supportedFields.Clear();
                    _fieldTypes.Clear();
                };
                _sdk.OnException += (ex) => Console.WriteLine($"[IRSDKSharper] SDK Exception: {ex.Message}");

                _sdk.Start();
                Thread.Sleep(100);

                if (_sdk != null && _sdk.IsStarted)
                {
                    _isInitialized = true;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IRSDKSharper] Exception during initialization: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private void DiscoverFields()
        {
            if (_fieldsDiscovered) return;

            try
            {
                Console.WriteLine("[IRSDKSharper] Attempting field discovery...");

                // Check if SDK and data are available
                if (_sdk?.Data?.TelemetryDataProperties == null)
                {
                    Console.WriteLine("[IRSDKSharper] TelemetryDataProperties not yet available, discovery deferred");
                    return;
                }

                // Check if TelemetryDataProperties has any content
                if (_sdk.Data.TelemetryDataProperties.Count == 0)
                {
                    Console.WriteLine("[IRSDKSharper] TelemetryDataProperties is empty, discovery deferred");
                    return;
                }

                Console.WriteLine("[IRSDKSharper] Discovering available telemetry fields...");

                _supportedFields.Clear();
                _fieldTypes.Clear();

                foreach (var prop in _sdk.Data.TelemetryDataProperties)
                {
                    var fieldName = prop.Key;
                    _supportedFields.Add(fieldName);

                    Type fieldType = typeof(object);
                    try
                    {
                        var typeProperty = prop.Value?.GetType().GetProperty("Type");
                        if (typeProperty != null)
                        {
                            var iRacingType = typeProperty.GetValue(prop.Value);
                            fieldType = MapIRacingTypeToNetType(iRacingType?.ToString());
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IRSDKSharper] Warning: Could not determine type for field '{fieldName}': {ex.Message}");
                    }

                    _fieldTypes[fieldName] = fieldType;
                }

                // Log priority field availability for overlay development
                var availablePriorityFields = OverlayPriorityFields.Where(f => _supportedFields.Contains(f)).ToList();
                var missingPriorityFields = OverlayPriorityFields.Where(f => !_supportedFields.Contains(f)).ToList();

                Console.WriteLine($"[IRSDKSharper] Field Discovery Complete:");
                Console.WriteLine($"  Total fields available: {_supportedFields.Count}");
                Console.WriteLine($"  Priority fields available: {availablePriorityFields.Count}/{OverlayPriorityFields.Length}");

                if (missingPriorityFields.Any() && missingPriorityFields.Count <= 10)
                {
                    Console.WriteLine($"  Missing priority fields: {string.Join(", ", missingPriorityFields)}");
                }
                else if (missingPriorityFields.Count > 10)
                {
                    Console.WriteLine($"  Missing priority fields: {missingPriorityFields.Count} fields missing");
                }

                _fieldsDiscovered = true;
                Console.WriteLine("[IRSDKSharper] Field discovery successful!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IRSDKSharper] Error during field discovery: {ex.Message}");
                Console.WriteLine($"[IRSDKSharper] Stack trace: {ex.StackTrace}");
            }
        }

        private Type MapIRacingTypeToNetType(string iRacingType)
        {
            return iRacingType?.ToLower() switch
            {
                "float" => typeof(float),
                "double" => typeof(double),
                "int" => typeof(int),
                "bool" => typeof(bool),
                "char" => typeof(char),
                "bitfield" => typeof(uint),
                _ => typeof(object)
            };
        }

        public HashSet<string> GetSupportedFields()
        {
            if (!_fieldsDiscovered && IsConnected) DiscoverFields();
            return new HashSet<string>(_supportedFields);
        }

        public Dictionary<string, Type> GetFieldTypes()
        {
            if (!_fieldsDiscovered && IsConnected) DiscoverFields();
            return new Dictionary<string, Type>(_fieldTypes);
        }

        public bool SupportsField(string fieldName)
        {
            if (!_fieldsDiscovered && IsConnected) DiscoverFields();
            return _supportedFields.Contains(fieldName);
        }

        public TelemetrySnapshot GetSnapshot()
        {
            if (!_isInitialized || _sdk == null)
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

            _pollTimer.Restart();

            try
            {
                // Retry field discovery if not yet complete and we're connected
                if (!_fieldsDiscovered && IsConnected)
                {
                    DiscoverFields();
                }

                var telemetry = BuildAdaptiveTelemetryDict();
                var sessionData = _sdk?.Data?.SessionInfoYaml ?? string.Empty;

                _pollTimer.Stop();
                LastPollDuration = _pollTimer.Elapsed;

                // Extract SessionTick with better error handling
                int sessionTick = 0;
                if (telemetry.ContainsKey("SessionTick"))
                {
                    try
                    {
                        sessionTick = Convert.ToInt32(telemetry["SessionTick"]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IRSDKSharper] Warning: Could not convert SessionTick to int: {ex.Message}");
                    }
                }

                return new TelemetrySnapshot
                {
                    IsValid = telemetry.Count > 0, // Only valid if we actually got some data
                    Timestamp = DateTime.UtcNow,
                    SessionTick = sessionTick,
                    RawTelemetryData = telemetry,
                    RawSessionData = sessionData
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IRSDKSharper] Error getting snapshot: {ex.Message}");
                _pollTimer.Stop();
                LastPollDuration = _pollTimer.Elapsed;
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
                Console.WriteLine($"[IRSDKSharper] Error parsing car numbers from SessionData: {ex.Message}");
            }
        }

        private Dictionary<string, object> BuildAdaptiveTelemetryDict()
        {
            var dict = new Dictionary<string, object>();

            try
            {
                if (_sdk?.Data?.TelemetryDataProperties == null)
                {
                    Console.WriteLine("[IRSDKSharper] TelemetryDataProperties is null");
                    return dict;
                }

                // CRITICAL FIX: Verify TelemetryDataProperties has content before proceeding
                if (_sdk.Data.TelemetryDataProperties.Count == 0)
                {
                    Console.WriteLine("[IRSDKSharper] TelemetryDataProperties.Count is 0 - no telemetry data available");
                    return dict;
                }

                // If we don't have discovered fields yet, try to use all available fields
                var fieldsToRequest = _fieldsDiscovered
                    ? OverlayPriorityFields.Where(f => _supportedFields.Contains(f))
                    : OverlayPriorityFields; // Try all priority fields if discovery incomplete

                int successCount = 0;
                int failureCount = 0;

                foreach (var fieldName in fieldsToRequest)
                {
                    try
                    {
                        // Check if field exists in TelemetryDataProperties before attempting GetValue
                        if (!_sdk.Data.TelemetryDataProperties.ContainsKey(fieldName))
                        {
                            // Field doesn't exist in the current telemetry data
                            if (!_loggedMissingFields.Contains(fieldName) && _loggedMissingFields.Count < 10)
                            {
                                Console.WriteLine($"[IRSDKSharper] Field '{fieldName}' not found in TelemetryDataProperties");
                                _loggedMissingFields.Add(fieldName);
                            }
                            continue;
                        }

                        var value = _sdk.Data.GetValue(fieldName);
                        if (value != null)
                        {
                            // Normalize arrays for JSON serialization consistency
                            if (value is Array arr)
                            {
                                dict[fieldName] = arr.Cast<object>().ToArray(); // ensure JSON-safe
                            }
                            else
                            {
                                dict[fieldName] = value;
                            }
                            successCount++;
                        }
                        else
                        {
                            // Field exists but returned null - still add it to maintain consistency
                            dict[fieldName] = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        failureCount++;
                        // Only log first few errors to avoid spam
                        if (!_loggedMissingFields.Contains(fieldName) && _loggedMissingFields.Count < 10)
                        {
                            Console.WriteLine($"[IRSDKSharper] Error getting field '{fieldName}': {ex.Message}");
                            _loggedMissingFields.Add(fieldName);
                        }
                    }
                }

                // Add synthetic CarIdxCarNumber from SessionData
                try
                {
                    var sessionData = _sdk?.Data?.SessionInfoYaml ?? string.Empty;
                    if (!string.IsNullOrEmpty(sessionData))
                    {
                        UpdateCarNumbersCache(sessionData);
                        dict["CarIdxCarNumber"] = _cachedCarNumbers.Cast<object>().ToArray();
                        successCount++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IRSDKSharper] Error extracting car numbers from SessionData: {ex.Message}");
                }

                // Enhanced logging every 100th sample to show actual data extraction success
                if (++_telemetryLogCounter % 100 == 0)
                {
                    Console.WriteLine($"[{Name}] Telemetry extraction: {successCount} successful, {failureCount} failed, {dict.Count} total fields");

                    // Log a few sample values for debugging
                    var sampleFields = new[] { "SessionTick", "SessionTime", "SessionState", "Speed", "RPM" };
                    var samples = sampleFields.Where(f => dict.ContainsKey(f))
                                            .Select(f => $"{f}={dict[f]}")
                                            .Take(3);
                    if (samples.Any())
                    {
                        Console.WriteLine($"[{Name}] Sample values: {string.Join(", ", samples)}");
                    }

                    // Enhanced CarIdx array debugging
                    if (dict.TryGetValue("CarIdxLapDistPct", out var dist) && dist is Array distArr)
                    {
                        Console.WriteLine($"[IRSDKSharper] CarIdxLapDistPct[0]: {distArr.GetValue(0)} (Len={distArr.Length})");
                    }

                    if (dict.TryGetValue("CarIdxPosition", out var pos) && pos is Array posArr)
                    {
                        Console.WriteLine($"[IRSDKSharper] CarIdxPosition[0]: {posArr.GetValue(0)} (Len={posArr.Length})");
                    }

                    if (dict.TryGetValue("CarIdxCarNumber", out var carNum) && carNum is Array carNumArr)
                    {
                        Console.WriteLine($"[IRSDKSharper] CarIdxCarNumber[0]: {carNumArr.GetValue(0)} (Len={carNumArr.Length})");
                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IRSDKSharper] Error building adaptive telemetry dictionary: {ex.Message}");
            }

            return dict;
        }

        public void Shutdown()
        {
            try
            {
                if (_sdk != null)
                {
                    Console.WriteLine("[IRSDKSharper] Shutting down SDK...");
                    _sdk.Stop();
                    _sdk = null;
                }
                _isInitialized = false;
                _fieldsDiscovered = false;
                _supportedFields.Clear();
                _fieldTypes.Clear();
                _loggedMissingFields.Clear();
                Array.Fill(_cachedCarNumbers, 0);
                _lastSessionDataHash = string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IRSDKSharper] Error during shutdown: {ex.Message}");
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
}