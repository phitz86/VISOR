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

        private readonly StreamWriter _telemetryWriter;
        private readonly object _telemetryLock = new();

        private readonly StreamWriter _f2TimeWriter;
        private readonly object _f2TimeLock = new();

        public SessionDataLogger(Func<string> getSessionYaml, Func<Dictionary<string, Type>> getFieldTypes)
        {
            _getSessionYaml = getSessionYaml;
            _getFieldTypes = getFieldTypes;

            _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VISOR_SessionData");
            Directory.CreateDirectory(_outputDirectory);

            Debug.WriteLine($"[SessionLogger] Output directory: {_outputDirectory}");

            // Initialize the CarLeftRight telemetry log file
            var telemetryFilePath = Path.Combine(_outputDirectory, $"CarLeftRight_telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var fsClr = new FileStream(telemetryFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _telemetryWriter = new StreamWriter(fsClr, Encoding.UTF8);
            _telemetryWriter.WriteLine("SessionTime,PlayerCarIdx,TargetCarIdx,CarLeftRight_Value,PlayerLapDistPct,TargetLapDistPct,LapDistDelta");
            _telemetryWriter.Flush();

            // Initialize the F2Time telemetry log file
            var f2TimeFilePath = Path.Combine(_outputDirectory, $"F2Time_telemetry_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var fsF2 = new FileStream(f2TimeFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            _f2TimeWriter = new StreamWriter(fsF2, Encoding.UTF8);
            _f2TimeWriter.WriteLine("SessionTime,PlayerCarIdx,TargetCarIdx,F2Time_Value,PlayerLapDistPct,TargetLapDistPct,LapDistDelta");
            _f2TimeWriter.Flush();
        }

        public void LogTelemetryFrame(double sessionTime, int playerCarIdx, int targetCarIdx, float clrValue, float playerLapDist, float targetLapDist)
        {
            if (_isDisposed) return;

            lock (_telemetryLock)
            {
                if (_telemetryWriter != null)
                {
                    // Handle single value case (when targetCarIdx is -1)
                    string targetCarStr = targetCarIdx == -1 ? "SINGLE" : targetCarIdx.ToString();
                    float lapDistDelta = targetCarIdx == -1 ? 0f : playerLapDist - targetLapDist;

                    _telemetryWriter.WriteLine($"{sessionTime:F3},{playerCarIdx},{targetCarStr},{clrValue:F4},{playerLapDist:F4},{targetLapDist:F4},{lapDistDelta:F4}");
                    _telemetryWriter.Flush();
                }
            }
        }

        public void LogF2TimeFrame(double sessionTime, int playerCarIdx, int targetCarIdx, float f2TimeValue, float playerLapDist, float targetLapDist)
        {
            if (_isDisposed) return;

            lock (_f2TimeLock)
            {
                if (_f2TimeWriter != null)
                {
                    float lapDistDelta = playerLapDist - targetLapDist;
                    _f2TimeWriter.WriteLine($"{sessionTime:F3},{playerCarIdx},{targetCarIdx},{f2TimeValue:F4},{playerLapDist:F4},{targetLapDist:F4},{lapDistDelta:F4}");
                    _f2TimeWriter.Flush();
                }
            }
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

            lock (_f2TimeLock)
            {
                _f2TimeWriter?.Flush();
                _f2TimeWriter?.Dispose();
            }

            Debug.WriteLine($"[SessionLogger] Disposed with {_loggedSessions.Count} sessions logged");
        }
    }
}