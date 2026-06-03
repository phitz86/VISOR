using System;
using System.Collections.Generic;
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

        public SessionDataLogger(Func<string> getSessionYaml)
        {
            _getSessionYaml = getSessionYaml;

            _outputDirectory = Path.Combine(Log.GetDiagnosticsDirectory(), "SessionData");
            Directory.CreateDirectory(_outputDirectory);

            Log.Debug($"[SessionLogger] Output directory: {_outputDirectory}");
        }

        public void ScheduleSessionAwareLogs(int sessionNum, string sessionType, double sessionTimeSeconds)
        {
            if (_isDisposed) return;

            if (_loggedSessions.Contains(sessionNum))
            {
                Log.Debug($"[SessionLogger] Already scheduled logging for session {sessionNum}, skipping");
                return;
            }

            _loggedSessions.Add(sessionNum);

            if (sessionTimeSeconds <= 0)
            {
                Log.Debug($"[SessionLogger] Invalid session time ({sessionTimeSeconds}s) for {sessionType}, using fallback logging");
                ScheduleLogForSession(sessionNum, sessionType);
                return;
            }

            Log.Debug($"[SessionLogger] Scheduling session-aware logs for {sessionType} ({sessionTimeSeconds}s duration):");

            ScheduleLogAtInterval(sessionNum, sessionType, TimeSpan.FromMinutes(2), "early");
            Log.Debug($"[SessionLogger]   Early log: 2 minutes");

            if (sessionTimeSeconds > 600)
            {
                var midTime = TimeSpan.FromSeconds(sessionTimeSeconds * 0.6);
                ScheduleLogAtInterval(sessionNum, sessionType, midTime, "mid");
                Log.Debug($"[SessionLogger]   Mid log: {midTime.TotalMinutes:F1} minutes");
            }

            var endTime = TimeSpan.FromSeconds(Math.Max(sessionTimeSeconds - 30, sessionTimeSeconds * 0.9));
            ScheduleLogAtInterval(sessionNum, sessionType, endTime, "late");
            Log.Debug($"[SessionLogger]   Late log: {endTime.TotalMinutes:F1} minutes");
        }

        public void ScheduleLogForSession(int sessionNum, string sessionType)
        {
            if (_isDisposed) return;

            if (_loggedSessions.Contains(sessionNum))
            {
                Log.Debug($"[SessionLogger] Already scheduled logging for session {sessionNum}, skipping");
                return;
            }

            _loggedSessions.Add(sessionNum);
            Log.Debug($"[SessionLogger] Scheduling simple log for SessionNum {sessionNum} ({sessionType}) in 2 minutes");

            ScheduleLogAtInterval(sessionNum, sessionType, TimeSpan.FromMinutes(2), "simple");
        }

        private void ScheduleLogAtInterval(int sessionNum, string sessionType, TimeSpan delay, string suffix)
        {
            if (_isDisposed) return;

            var timer = new System.Timers.Timer(delay.TotalMilliseconds) { AutoReset = false };
            timer.Elapsed += async (sender, e) =>
            {
                Log.Debug($"[SessionLogger] Timer elapsed - executing {suffix} log for {sessionType}");
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
                Log.Debug($"[SessionLogger] Files saved to: {_outputDirectory}");
            }
            catch (Exception ex)
            {
                Log.Error($"[SessionLogger] Exception during {suffix} logging", ex);
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
                Log.Error($"[SessionLogger] Error writing {suffix} YAML", ex);
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
            Log.Debug("[SessionLogger] Disposing - cleaning up active timers");

            lock (_activeTimers)
            {
                foreach (var timer in _activeTimers)
                {
                    timer?.Stop();
                    timer?.Dispose();
                }
                _activeTimers.Clear();
            }

            Log.Debug($"[SessionLogger] Disposed with {_loggedSessions.Count} sessions logged");
        }
    }
}