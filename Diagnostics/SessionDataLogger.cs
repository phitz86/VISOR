using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Timers;

namespace VISOR.Diagnostics
{
    /// <summary>
    /// Session-aware data logger that creates multi-interval logs based on session duration
    /// Adapts logging intervals to session length for optimal data capture
    /// </summary>
    public class SessionDataLogger : IDisposable
    {
        private readonly string _outputDirectory;
        private readonly List<System.Timers.Timer> _activeTimers = new();
        private readonly HashSet<int> _loggedSessions = new();
        private bool _isDisposed = false;

        private readonly Func<string> _getSessionYaml;
        private readonly Func<Dictionary<string, Type>> _getFieldTypes;

        public SessionDataLogger(Func<string> getSessionYaml, Func<Dictionary<string, Type>> getFieldTypes)
        {
            _getSessionYaml = getSessionYaml;
            _getFieldTypes = getFieldTypes;

            // Create output directory
            _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VISOR_SessionData");
            Directory.CreateDirectory(_outputDirectory);

            Debug.WriteLine($"[SessionLogger] Output directory: {_outputDirectory}");
        }

        /// <summary>
        /// Schedule session-aware logs based on session duration
        /// Creates multiple log points optimized for different session lengths
        /// </summary>
        public void ScheduleSessionAwareLogs(int sessionNum, string sessionType, double sessionTimeSeconds)
        {
            if (_isDisposed) return;

            if (_loggedSessions.Contains(sessionNum))
            {
                Debug.WriteLine($"[SessionLogger] Already scheduled logging for session {sessionNum}, skipping");
                return;
            }

            _loggedSessions.Add(sessionNum);

            if (sessionTimeSeconds <= 0)
            {
                Debug.WriteLine($"[SessionLogger] Invalid session time ({sessionTimeSeconds}s) for {sessionType}, using fallback logging");
                ScheduleLogForSession(sessionNum, sessionType); // Fallback to simple 2-minute log
                return;
            }

            Debug.WriteLine($"[SessionLogger] Scheduling session-aware logs for {sessionType} ({sessionTimeSeconds}s duration):");

            // Always log early (2 minutes)
            ScheduleLogAtInterval(sessionNum, sessionType, TimeSpan.FromMinutes(2), "early");
            Debug.WriteLine($"[SessionLogger]   Early log: 2 minutes");

            // If session > 10 minutes, add a mid-session log
            if (sessionTimeSeconds > 600) // 10 minutes
            {
                var midTime = TimeSpan.FromSeconds(sessionTimeSeconds * 0.6); // 60% through session
                ScheduleLogAtInterval(sessionNum, sessionType, midTime, "mid");
                Debug.WriteLine($"[SessionLogger]   Mid log: {midTime.TotalMinutes:F1} minutes");
            }

            // Always log near the end (30 seconds before session ends, or 90% through if very short)
            var endTime = TimeSpan.FromSeconds(Math.Max(sessionTimeSeconds - 30, sessionTimeSeconds * 0.9));
            ScheduleLogAtInterval(sessionNum, sessionType, endTime, "late");
            Debug.WriteLine($"[SessionLogger]   Late log: {endTime.TotalMinutes:F1} minutes");

            Debug.WriteLine($"[SessionLogger] Total scheduled logs for {sessionType}: {(sessionTimeSeconds > 600 ? 3 : 2)}");
        }

        /// <summary>
        /// Legacy method for backward compatibility - uses simple 2-minute delay
        /// </summary>
        public void ScheduleLogForSession(int sessionNum, string sessionType)
        {
            if (_isDisposed) return;

            if (_loggedSessions.Contains(sessionNum))
            {
                Debug.WriteLine($"[SessionLogger] Already scheduled logging for session {sessionNum}, skipping");
                return;
            }

            _loggedSessions.Add(sessionNum);
            Debug.WriteLine($"[SessionLogger] Scheduling simple log for SessionNum {sessionNum} ({sessionType}) in 2 minutes");

            ScheduleLogAtInterval(sessionNum, sessionType, TimeSpan.FromMinutes(2), "simple");
        }

        /// <summary>
        /// Schedule a log at a specific interval with a suffix for the filename
        /// </summary>
        private void ScheduleLogAtInterval(int sessionNum, string sessionType, TimeSpan delay, string suffix)
        {
            if (_isDisposed) return;

            var timer = new System.Timers.Timer(delay.TotalMilliseconds);
            timer.Elapsed += async (sender, e) =>
            {
                Debug.WriteLine($"[SessionLogger] Timer elapsed - executing {suffix} log for {sessionType}");
                await ExecuteLog(sessionNum, sessionType, suffix);

                // Clean up this timer
                timer.Stop();
                timer.Dispose();
                lock (_activeTimers)
                {
                    _activeTimers.Remove(timer);
                }
            };

            timer.AutoReset = false;

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

        /// <summary>
        /// Execute the session data logging with suffix for multiple logs per session
        /// </summary>
        private async Task ExecuteLog(int sessionNum, string sessionType, string suffix)
        {
            if (_isDisposed) return;

            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string sessionName = GetSessionName(sessionNum);

                // Log YAML data with suffix
                await LogSessionYaml(sessionName, timestamp, suffix);

                // Log available telemetry fields (only once per session - on early log)
                if (suffix == "early" || suffix == "simple")
                {
                    await LogTelemetryFields(sessionName, timestamp);
                }

                Debug.WriteLine($"[SessionLogger] {suffix} logging completed successfully for {sessionName}");
                Debug.WriteLine($"[SessionLogger] Files saved to: {_outputDirectory}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Exception during {suffix} logging: {ex.Message}");
                Debug.WriteLine($"[SessionLogger] Stack trace: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Log the session YAML data to file with suffix
        /// </summary>
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
                    Debug.WriteLine($"[SessionLogger] YAML data written to: {filepath}");
                }
                else
                {
                    Debug.WriteLine($"[SessionLogger] No YAML data available for {suffix} log");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Error writing {suffix} YAML: {ex.Message}");
            }
        }

        /// <summary>
        /// Log available telemetry field information
        /// </summary>
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
                        await writer.WriteLineAsync($"Total Fields: {fieldTypes.Count}");
                        await writer.WriteLineAsync();

                        await writer.WriteLineAsync("Field Name\tType");
                        await writer.WriteLineAsync("----------\t----");

                        foreach (var kvp in fieldTypes)
                        {
                            string typeName = kvp.Value.Name;
                            if (kvp.Value.IsArray)
                                typeName = $"{kvp.Value.GetElementType()?.Name ?? "Unknown"}[]";

                            await writer.WriteLineAsync($"{kvp.Key}\t{typeName}");
                        }
                    }

                    Debug.WriteLine($"[SessionLogger] Field data written to: {filepath}");
                }
                else
                {
                    Debug.WriteLine("[SessionLogger] No field type data available to log");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Error writing field data: {ex.Message}");
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

        /// <summary>
        /// Clean up all active timers
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;

            Debug.WriteLine("[SessionLogger] Disposing - cleaning up active timers");
            _isDisposed = true;

            lock (_activeTimers)
            {
                foreach (var timer in _activeTimers)
                {
                    timer?.Stop();
                    timer?.Dispose();
                }
                _activeTimers.Clear();
            }

            Debug.WriteLine($"[SessionLogger] Disposed with {_loggedSessions.Count} sessions logged");
        }
    }
}