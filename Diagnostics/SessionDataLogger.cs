using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Timers;

namespace VISOR.Diagnostics
{
    /// <summary>
    /// Simple session data logger that dumps YAML and telemetry info
    /// for debugging what data is available during different session types
    /// </summary>
    public class SessionDataLogger : IDisposable
    {
        private readonly string _outputDirectory;
        private System.Timers.Timer _dumpTimer;
        private int _lastLoggedSessionNum = -1;
        private bool _isLogging = false;
        private Func<string> _getSessionYaml;
        private Func<System.Collections.Generic.Dictionary<string, Type>> _getFieldTypes;

        public SessionDataLogger(Func<string> getSessionYaml, Func<System.Collections.Generic.Dictionary<string, Type>> getFieldTypes)
        {
            _getSessionYaml = getSessionYaml;
            _getFieldTypes = getFieldTypes;

            // Create output directory
            _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "VISOR_SessionData");
            Directory.CreateDirectory(_outputDirectory);

            Debug.WriteLine($"[SessionLogger] Output directory: {_outputDirectory}");
        }

        /// <summary>
        /// Schedule a data dump for 2 minutes after a session change
        /// </summary>
        public void ScheduleLogForSession(int sessionNum, string sessionType)
        {
            if (_isLogging || sessionNum == _lastLoggedSessionNum)
            {
                Debug.WriteLine($"[SessionLogger] Already logged or logging for session {sessionNum}, skipping");
                return;
            }

            Debug.WriteLine($"[SessionLogger] Scheduling log for SessionNum {sessionNum} ({sessionType}) in 2 minutes");

            // Cancel any existing timer
            _dumpTimer?.Stop();
            _dumpTimer?.Dispose();

            // Create new timer for 2 minutes
            _dumpTimer = new System.Timers.Timer(120000);
            _dumpTimer.Elapsed += async (sender, e) =>
            {
                Debug.WriteLine($"[SessionLogger] Timer elapsed - executing log for {sessionType}");
                await ExecuteLog(sessionNum, sessionType);
                _dumpTimer?.Stop();
                _dumpTimer?.Dispose();
            };
            _dumpTimer.AutoReset = false;
            _dumpTimer.Start();
        }

        /// <summary>
        /// Execute the session data logging
        /// </summary>
        private async Task ExecuteLog(int sessionNum, string sessionType)
        {
            if (_isLogging) return;

            _isLogging = true;
            _lastLoggedSessionNum = sessionNum;

            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string sessionName = GetSessionName(sessionNum);

                // Log YAML data
                await LogSessionYaml(sessionName, timestamp);

                // Log available telemetry fields
                await LogTelemetryFields(sessionName, timestamp);

                Debug.WriteLine($"[SessionLogger] Logging completed successfully for {sessionName}");
                Debug.WriteLine($"[SessionLogger] Files saved to: {_outputDirectory}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Exception during logging: {ex.Message}");
                Debug.WriteLine($"[SessionLogger] Stack trace: {ex.StackTrace}");
            }
            finally
            {
                _isLogging = false;
            }
        }

        /// <summary>
        /// Log the session YAML data to file
        /// </summary>
        private async Task LogSessionYaml(string sessionName, string timestamp)
        {
            try
            {
                string yamlData = _getSessionYaml?.Invoke();
                if (!string.IsNullOrEmpty(yamlData))
                {
                    string filename = $"Session_{sessionName}_{timestamp}.yaml";
                    string filepath = Path.Combine(_outputDirectory, filename);

                    await File.WriteAllTextAsync(filepath, yamlData);
                    Debug.WriteLine($"[SessionLogger] YAML data written to: {filepath}");
                }
                else
                {
                    Debug.WriteLine("[SessionLogger] No YAML data available to log");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLogger] Error writing YAML: {ex.Message}");
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
        /// Clean up timers
        /// </summary>
        public void Dispose()
        {
            Debug.WriteLine("[SessionLogger] Disposing");
            _dumpTimer?.Stop();
            _dumpTimer?.Dispose();
        }
    }
}