using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VISOR.Telemetry;

namespace VISOR.Diagnostics
{
    public class TelemetrySnapshotLogger
    {
        private readonly string _outputDirectory;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private readonly Dictionary<string, int> _logCounts = new();
        private readonly Dictionary<string, DateTime> _lastLogTimes = new();
        private readonly object _lockObject = new();

        public TelemetrySnapshotLogger(string outputDirectory)
        {
            _outputDirectory = outputDirectory ?? "logs";
            Directory.CreateDirectory(_outputDirectory);
            Console.WriteLine($"[Logger] Initialized with output directory: {Path.GetFullPath(_outputDirectory)}");
        }

        public string GetOutputDirectory() => _outputDirectory;

        private Dictionary<string, object> ScrubFields(Dictionary<string, object> raw)
        {
            var safe = new Dictionary<string, object>();

            if (raw == null) return safe;

            foreach (var kv in raw)
            {
                try
                {
                    if (kv.Value == null) continue;

                    var type = kv.Value.GetType();

                    // Exclude known problem types
                    if (typeof(System.Diagnostics.PerformanceCounter).IsAssignableFrom(type) ||
                        typeof(System.Threading.Tasks.Task).IsAssignableFrom(type) ||
                        typeof(Delegate).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    // Serialize-friendly check (optional)
                    // You can expand this to whitelist primitive types only if needed
                    safe[kv.Key] = kv.Value;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Logger] Skipping field {kv.Key} due to exception: {ex.Message}");
                }
            }

            return safe;
        }

        public void LogSnapshot(string wrapperName, DateTime timestamp, SVappsLABSnapshot snapshot,
            TimeSpan duration, long memoryBytes, double cpuPercent)
        {
            if (string.IsNullOrEmpty(wrapperName) || snapshot == null)
            {
                return;
            }

            lock (_lockObject)
            {
                try
                {
                    var logPath = Path.Combine(_outputDirectory, $"VISOR-{Sanitize(wrapperName)}.jsonl");

                    // Scrub telemetry fields to avoid serialization issues
                    var safeFields = ScrubFields(snapshot.RawTelemetryData);

                    var logEntry = new
                    {
                        timestamp = timestamp.ToString("o"),
                        wrapper = wrapperName,
                        isConnected = snapshot.IsValid,
                        sessionTick = snapshot.SessionTick,
                        sessionTime = snapshot.SessionTime,
                        sessionTimeRemain = snapshot.SessionTimeRemain,
                        playerCarIdx = snapshot.PlayerCarIdx,
                        sessionState = snapshot?.SessionState ?? string.Empty,
                        performance = new
                        {
                            durationMs = Math.Round(duration.TotalMilliseconds, 2),
                            memoryBytes = memoryBytes,
                            cpuPercent = Math.Round(cpuPercent, 2)
                        },
                        fieldCount = safeFields.Count,
                        fields = safeFields
                    };

                    string json;

                    try
                    {
                        json = JsonSerializer.Serialize(logEntry, _jsonOptions);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Logger] ? Serialization failed for wrapper: {wrapperName}");
                        Console.WriteLine($"  Exception: {ex.GetType().Name}: {ex.Message}");

                        Console.WriteLine($"  ??? IsValid: {snapshot?.IsValid}");
                        Console.WriteLine($"  ??? SessionTick: {snapshot?.SessionTick}");
                        Console.WriteLine($"  ??? SessionTime: {snapshot?.SessionTime}");
                        Console.WriteLine($"  ??? SessionTimeRemain: {snapshot?.SessionTimeRemain}");
                        Console.WriteLine($"  ??? PlayerCarIdx: {snapshot?.PlayerCarIdx}");
                        Console.WriteLine($"  ??? SessionState: {snapshot?.SessionState ?? "<null>"}");
                        Console.WriteLine($"  ??? RawTelemetryData: {snapshot?.RawTelemetryData?.Count ?? -1} fields");

                        if (snapshot?.RawTelemetryData != null)
                        {
                            int i = 0;
                            foreach (var kv in snapshot.RawTelemetryData)
                            {
                                var typeName = kv.Value?.GetType().FullName ?? "<null>";
                                Console.WriteLine($"    [{i++}] {kv.Key}: {typeName}");
                            }
                        }

                        Console.WriteLine("[Logger] Skipping this log entry due to serialization failure.");
                        return;
                    }

                    File.AppendAllText(logPath, json + Environment.NewLine);

                    _logCounts[wrapperName] = _logCounts.GetValueOrDefault(wrapperName, 0) + 1;
                    _lastLogTimes[wrapperName] = timestamp;

                    if (_logCounts[wrapperName] % 1000 == 0)
                    {
                        var fileSize = new FileInfo(logPath).Length / (1024 * 1024); // MB
                        Console.WriteLine($"[Logger] {wrapperName}: {_logCounts[wrapperName]} entries logged ({fileSize:F1} MB)");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Logger] Error logging snapshot for {wrapperName}: {ex.Message}");

                    try
                    {
                        var errorLogPath = Path.Combine(_outputDirectory, "VISOR-errors.log");
                        var errorEntry = $"{timestamp:o} - {wrapperName} - Logging error: {ex.Message}{Environment.NewLine}";
                        File.AppendAllText(errorLogPath, errorEntry);
                    }
                    catch { }
                }
            }
        }

        public void LogSessionStart(DateTime startTime, List<string> wrapperNames, TimeSpan? maxDuration)
        {
            try
            {
                var sessionLogPath = Path.Combine(_outputDirectory, "VISOR-session.jsonl");
                var sessionEntry = new
                {
                    timestamp = startTime.ToString("o"),
                    eventType = "session_start",
                    wrappers = wrapperNames,
                    maxDuration = maxDuration?.ToString(),
                    systemInfo = new
                    {
                        os = Environment.OSVersion.VersionString,
                        dotnetVersion = Environment.Version.ToString(),
                        is64Bit = Environment.Is64BitProcess,
                        processId = Environment.ProcessId,
                        machineName = Environment.MachineName
                    }
                };

                var json = JsonSerializer.Serialize(sessionEntry, _jsonOptions);
                File.AppendAllText(sessionLogPath, json + Environment.NewLine);

                Console.WriteLine($"[Logger] Session started - logging to {Path.GetFullPath(_outputDirectory)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Logger] Error logging session start: {ex.Message}");
            }
        }

        public void LogSessionEnd(DateTime endTime, TimeSpan sessionDuration, Dictionary<string, int> pollCounts)
        {
            try
            {
                var sessionLogPath = Path.Combine(_outputDirectory, "VISOR-session.jsonl");
                var sessionEntry = new
                {
                    timestamp = endTime.ToString("o"),
                    eventType = "session_end",
                    sessionDuration = sessionDuration.ToString(),
                    totalLogs = _logCounts.Values.Sum(),
                    wrapperStats = _logCounts.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new
                        {
                            logsWritten = kvp.Value,
                            pollsExecuted = pollCounts.GetValueOrDefault(kvp.Key, 0),
                            lastLogTime = _lastLogTimes.GetValueOrDefault(kvp.Key, DateTime.MinValue).ToString("o")
                        }
                    )
                };

                var json = JsonSerializer.Serialize(sessionEntry, _jsonOptions);
                File.AppendAllText(sessionLogPath, json + Environment.NewLine);

                // Print summary
                Console.WriteLine($"[Logger] Session ended - Total logs written: {_logCounts.Values.Sum()}");
                foreach (var kvp in _logCounts.OrderByDescending(x => x.Value))
                {
                    var logPath = Path.Combine(_outputDirectory, $"VISOR-{Sanitize(kvp.Key)}.jsonl");
                    var fileSize = File.Exists(logPath) ? new FileInfo(logPath).Length / (1024 * 1024) : 0; // MB
                    Console.WriteLine($"  {kvp.Key}: {kvp.Value} logs ({fileSize:F1} MB)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Logger] Error logging session end: {ex.Message}");
            }
        }

        public void LogError(string wrapperName, string errorMessage, Exception exception = null)
        {
            try
            {
                var errorLogPath = Path.Combine(_outputDirectory, "VISOR-errors.log");
                var errorEntry = $"{DateTime.UtcNow:o} - {wrapperName} - {errorMessage}";

                if (exception != null)
                {
                    errorEntry += $" - Exception: {exception.Message}";
                    if (!string.IsNullOrEmpty(exception.StackTrace))
                    {
                        errorEntry += $"\nStack trace: {exception.StackTrace}";
                    }
                }

                errorEntry += Environment.NewLine;
                File.AppendAllText(errorLogPath, errorEntry);
            }
            catch
            {
                // Silently fail if we can't log errors
            }
        }

        public void CreateSummaryReport()
        {
            try
            {
                var reportPath = Path.Combine(_outputDirectory, $"VISOR-summary-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

                var report = new
                {
                    generatedAt = DateTime.UtcNow.ToString("o"),
                    outputDirectory = Path.GetFullPath(_outputDirectory),
                    totalLogFiles = _logCounts.Count,
                    totalLogEntries = _logCounts.Values.Sum(),
                    wrappers = _logCounts.Select(kvp => new
                    {
                        name = kvp.Key,
                        logCount = kvp.Value,
                        lastLogTime = _lastLogTimes.GetValueOrDefault(kvp.Key, DateTime.MinValue).ToString("o"),
                        logFile = $"VISOR-{Sanitize(kvp.Key)}.jsonl",
                        fileSizeMB = GetFileSizeMB($"VISOR-{Sanitize(kvp.Key)}.jsonl")
                    }).OrderByDescending(w => w.logCount).ToArray(),
                    files = Directory.GetFiles(_outputDirectory, "VISOR-*.jsonl")
                        .Select(f => new
                        {
                            name = Path.GetFileName(f),
                            sizeMB = new FileInfo(f).Length / (1024.0 * 1024.0),
                            lastModified = File.GetLastWriteTime(f).ToString("o")
                        }).ToArray()
                };

                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                File.WriteAllText(reportPath, json);
                Console.WriteLine($"[Logger] Summary report created: {reportPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Logger] Error creating summary report: {ex.Message}");
            }
        }

        private double GetFileSizeMB(string fileName)
        {
            try
            {
                var filePath = Path.Combine(_outputDirectory, fileName);
                return File.Exists(filePath) ? new FileInfo(filePath).Length / (1024.0 * 1024.0) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unknown";

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name.ToLower().Replace(' ', '_').Replace('.', '_');
        }

        public Dictionary<string, int> GetLogCounts() => new(_logCounts);

        public void Flush()
        {
            // Force any pending file operations to complete
            // In this implementation, we're using synchronous writes, so this is mostly a no-op
            // But we could add buffering in the future
        }
    }
}