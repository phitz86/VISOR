using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace VISOR.Diagnostics
{
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

            _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VISOR_SessionData");
            Directory.CreateDirectory(_outputDirectory);

            Debug.WriteLine($"[SessionLogger] Output directory: {_outputDirectory}");
        }

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
                ScheduleLogForSession(sessionNum, sessionType);
                return;
            }

            Debug.WriteLine($"[SessionLogger] Scheduling session-aware logs for {sessionType} ({sessionTimeSeconds}s duration):");

            ScheduleLogAtInterval(sessionNum, sessionType, TimeSpan.FromMinutes(2), "early");
            Debug.WriteLine($"[SessionLogger]   Early log: 2 minutes");

            if (sessionTimeSeconds > 600)
            {
                var midTime = TimeSpan.FromSeconds(sessionTimeSeconds * 0.6);
                ScheduleLogAtInterval(sessionNum, sessionType, midTime, "mid");
                Debug.WriteLine($"[SessionLogger]   Mid log: {midTime.TotalMinutes:F1} minutes");
            }

            var endTime = TimeSpan.FromSeconds(Math.Max(sessionTimeSeconds - 30, sessionTimeSeconds * 0.9));
            ScheduleLogAtInterval(sessionNum, sessionType, endTime, "late");
            Debug.WriteLine($"[SessionLogger]   Late log: {endTime.TotalMinutes:F1} minutes");
        }

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
            Debug.WriteLine("[SessionLogger] Disposing - cleaning up active timers");

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