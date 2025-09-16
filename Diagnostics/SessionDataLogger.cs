using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Timers;

namespace VISOR.Diagnostics
{
    /// <summary>
    /// Session-aware data logger that creates multi-interval logs and continuous telemetry logs.
    /// Adapts logging intervals to session length for optimal data capture.
    /// </summary>
    public class SessionDataLogger : IDisposable
    {
        private readonly string _outputDirectory;
        private readonly List<System.Timers.Timer> _activeTimers = new();
        private readonly HashSet<int> _loggedSessions = new();
        private bool _isDisposed = false;

        private readonly Func<string> _getSessionYaml;
        private readonly Func<Dictionary<string, Type>> _getFieldTypes;

        // Added for continuous telemetry logging
        private readonly StreamWriter _telemetryWriter;
        private readonly object _telemetryLock = new();

        public SessionDataLogger(Func<string> getSessionYaml, Func<Dictionary<string, Type>> getFieldTypes)
        {
            _getSessionYaml = getSessionYaml;
            _getFieldTypes = getFieldTypes;

            _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VISOR_SessionData");
            Directory.CreateDirectory(_outputDirectory);

            Debug.WriteLine($"[SessionLogger] Output directory: {_outputDirectory}");

            // Initialize the continuous telemetry log file
            var telemetryFilePath = Path.Combine(_outputDirectory, $"CarLeftRight_telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var fs = new FileStream(telemetryFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _telemetryWriter = new StreamWriter(fs);
            _telemetryWriter.WriteLine("SessionTime,PlayerCarIdx,TargetCarIdx,CarLeftRight_Value,PlayerLapDistPct,TargetLapDistPct,LapDistDelta");
            _telemetryWriter.Flush();
        }

        /// <summary>
        /// Logs a single frame of high-frequency telemetry data when a meaningful change is detected.
        /// </summary>
        public void LogTelemetryFrame(double sessionTime, int playerCarIdx, int targetCarIdx, float clrValue, float playerLapDist, float targetLapDist)
        {
            if (_isDisposed) return;

            lock (_telemetryLock)
            {
                if (_telemetryWriter != null)
                {
                    float lapDistDelta = playerLapDist - targetLapDist;
                    _telemetryWriter.WriteLine($"{sessionTime:F3},{playerCarIdx},{targetCarIdx},{clrValue:F4},{playerLapDist:F4},{targetLapDist:F4},{lapDistDelta:F4}");
                }
            }
        }

        /// <summary>
        /// Schedule session-aware logs based on session duration.
        /// </summary>
        public void ScheduleSessionAwareLogs(int sessionNum, string sessionType, double sessionTimeSeconds)
        {
            if (_isDisposed || _loggedSessions.Contains(sessionNum)) return;

            _loggedSessions.Add(sessionNum);

            if (sessionTimeSeconds <= 0)
            {
                Debug.WriteLine($"[SessionLogger] Invalid session time ({sessionTimeSeconds}s) for {sessionType}, using fallback logging");
                ScheduleLogForSession(sessionNum, sessionType); // Fallback to simple 2-minute log
                return;
            }

            Debug.WriteLine($"[SessionLogger] Scheduling session-aware logs for {sessionType} ({sessionTimeSeconds}s duration):");

            ScheduleLogAtInterval(sessionNum, sessionType, TimeSpan.FromMinutes(2), "early");
            Debug.WriteLine($"[SessionLogger]   Early log: 2 minutes");

            if (sessionTimeSeconds > 600) // 10 minutes
            {
                var midTime = TimeSpan.FromSeconds(sessionTimeSeconds * 0.6);
                ScheduleLogAtInterval(sessionNum, sessionType, midTime, "mid");
                Debug.WriteLine($"[SessionLogger]   Mid log: {midTime.TotalMinutes:F1} minutes");
            }

            var endTime = TimeSpan.FromSeconds(Math.Max(sessionTimeSeconds - 30, sessionTimeSeconds * 0.9));
            ScheduleLogAtInterval(sessionNum, sessionType, endTime, "late");
            Debug.WriteLine($"[SessionLogger]   Late log: {endTime.TotalMinutes:F1} minutes");
        }

        /// <summary>
        /// Legacy method for backward compatibility - uses simple 2-minute delay.
        /// </summary>
        public void ScheduleLogForSession(int sessionNum, string sessionType)
        {
            if (_isDisposed || _loggedSessions.Contains(sessionNum)) return;

            _loggedSessions.Add(sessionNum);
            Debug.WriteLine($"[SessionLogger] Scheduling simple log for SessionNum {sessionNum} ({sessionType}) in 2 minutes");

            ScheduleLogAtInterval(sessionNum, sessionType, TimeSpan.FromMinutes(2), "simple");
        }

        private void ScheduleLogAtInterval(int sessionNum, string sessionType, TimeSpan delay, string suffix)
        {
            if (_isDisposed) return;

            var timer = new System.Timers.Timer(delay.TotalMilliseconds) { AutoReset = false };
            timer.Elapsed += async (sender, e) =>
            {
                Debug.WriteLine($"[SessionLogger] Timer elapsed - executing {suffix} log for {sessionType}");
                await ExecuteLog(sessionNum, sessionType, suffix);

                timer.Stop();
                timer.Dispose();
                lock (_activeTimers)
                {
                    _activeTimers.Remove(timer);
                }
            };

            lock (_activeTimers)
            {
                if (!_isDisposed)
                {
                    _activeTimers.Add(timer);
                    timer.Start();
                }
                else
                {
                    timer.Dispose();
                }
            }
        }

        private async Task ExecuteLog(int sessionNum, string sessionType, string suffix)
        {
            if (_isDisposed) return;

            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string sessionName = GetSessionName(sessionNum);

                await LogSessionYaml(sessionName, timestamp, suffix);

                if (suffix == "early" || suffix == "simple")
                {
                    await LogTelemetryFields(sessionName, timestamp);
                }
                Debug.WriteLine($"[SessionLogger] Files saved to: {_outputDirectory}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Exception during {suffix} logging: {ex.Message}");
            }
        }

        private async Task LogSessionYaml(string sessionName, string timestamp, string suffix)
        {
            try
            {
                string yamlData = _getSessionYaml?.Invoke();
                if (!string.IsNullOrEmpty(yamlData))
                {
                    string filename = $"Session_{sessionName}_{timestamp}_{suffix}.yaml";
                    string filepath = Path.Combine(_outputDirectory, filename);
                    await File.WriteAllTextAsync(filepath, yamlData);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Error writing {suffix} YAML: {ex.Message}");
            }
        }

        private async Task LogTelemetryFields(string sessionName, string timestamp)
        {
            try
            {
                var fieldTypes = _getFieldTypes?.Invoke();
                if (fieldTypes != null && fieldTypes.Count > 0)
                {
                    string filename = $"Session_{sessionName}_{timestamp}_fields.txt";
                    string filepath = Path.Combine(_outputDirectory, filename);

                    using (var writer = new StreamWriter(filepath))
                    {
                        await writer.WriteLineAsync($"VISOR Telemetry Fields - {sessionName} Session");
                        await writer.WriteLineAsync($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        await writer.WriteLineAsync();
                        foreach (var kvp in fieldTypes)
                        {
                            string typeName = kvp.Value.IsArray ? $"{kvp.Value.GetElementType()?.Name ?? "Unknown"}[]" : kvp.Value.Name;
                            await writer.WriteLineAsync($"{kvp.Key,-30}\t{typeName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Error writing field data: {ex.Message}");
            }
        }

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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Debug.WriteLine("[SessionLogger] Disposing - cleaning up active timers and writers");

            lock (_activeTimers)
            {
                foreach (var timer in _activeTimers)
                {
                    timer?.Stop();
                    timer?.Dispose();
                }
                _activeTimers.Clear();
            }

            lock (_telemetryLock)
            {
                _telemetryWriter?.Flush();
                _telemetryWriter?.Dispose();
            }

            Debug.WriteLine($"[SessionLogger] Disposed with {_loggedSessions.Count} sessions logged");
        }
    }
}