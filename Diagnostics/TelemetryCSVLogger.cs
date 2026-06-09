using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VISOR.Telemetry;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: Logs telemetry data to CSV for analysis of CarIdxEstTime and proximity calculations.
    /// Samples at 1Hz (once per second) to keep file sizes manageable.
    /// </summary>
    public class TelemetryCSVLogger : IDisposable
    {
        private readonly string _outputDirectory;
        private string _currentCsvPath = null!;
        private StreamWriter? _writer;
        private DateTime _lastLogTime = DateTime.MinValue;
        private readonly TimeSpan _logInterval = TimeSpan.FromSeconds(1.0); // 1Hz sampling
        private int _lastSessionNum = -1;
        private bool _isDisposed = false;
        private bool _headerWritten = false;

        public TelemetryCSVLogger()
        {
            _outputDirectory = Path.Combine(Log.GetDiagnosticsDirectory(), "Telemetry");
            Directory.CreateDirectory(_outputDirectory);
            Log.Info($"[TelemetryCSV] Output directory: {_outputDirectory}");
        }

        /// <summary>
        /// Log a telemetry snapshot. Internally throttled to 1Hz.
        /// </summary>
        public void LogSnapshot(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            if (_isDisposed || snapshot == null || !snapshot.IsValid || dataProvider == null || !dataProvider.IsDataReady)
                return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastLogTime < _logInterval)
                return;

            _lastLogTime = now;

            try
            {
                int currentSessionNum = snapshot.SessionNum;

                if (currentSessionNum != _lastSessionNum)
                {
                    StartNewSessionFile(currentSessionNum, dataProvider);
                    _lastSessionNum = currentSessionNum;
                }

                if (!_headerWritten && _writer != null)
                {
                    WriteHeader();
                    _headerWritten = true;
                }

                if (_writer != null)
                {
                    WriteDataRows(snapshot, dataProvider, now);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[TelemetryCSV] Error logging snapshot: {ex.Message}");
            }
        }

        private void StartNewSessionFile(int sessionNum, ISessionDataProvider dataProvider)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                _headerWritten = false;

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string sessionName = GetSessionNameFromNumber(sessionNum);
                string filename = $"Telemetry_{sessionName}_{timestamp}.csv";
                _currentCsvPath = Path.Combine(_outputDirectory, filename);

                _writer = new StreamWriter(_currentCsvPath, false, Encoding.UTF8);
                Log.Info($"[TelemetryCSV] Started new log file: {filename}");
            }
            catch (Exception ex)
            {
                Log.Error($"[TelemetryCSV] Failed to start new session file", ex);
            }
        }

        private void WriteHeader()
        {
            if (_writer == null) return;
            var header = new StringBuilder();
            header.Append("Timestamp,");
            header.Append("SessionTime,");
            header.Append("PlayerCarIdx,");
            header.Append("CarIdx,");
            header.Append("CarNumber,");
            header.Append("DriverName,");
            header.Append("CarIdxEstTime,");
            header.Append("CarIdxLapDistPct,");
            header.Append("CarIdxLap,");
            header.Append("CarIdxLastLapTime,");
            header.Append("CarIdxOnPitRoad,");
            header.Append("ProximityDistance");

            _writer.WriteLine(header.ToString());
            _writer.Flush();
        }

        private void WriteDataRows(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider, DateTime timestamp)
        {
            if (_writer == null) return;
            try
            {
                int playerCarIdx = snapshot.PlayerCarIdx;
                if (playerCarIdx < 0) return;

                float sessionTime = (float)snapshot.SessionTime;
                string timestampStr = timestamp.ToString("yyyyMMdd HH:mm:ss.fff");

                var carIdxEstTime = snapshot.CarIdxEstTime;
                var carIdxLapDistPct = snapshot.CarIdxLapDistPct;
                var carIdxLap = snapshot.CarIdxLap;
                var carIdxLastLapTime = snapshot.CarIdxLastLapTime;
                var carIdxOnPitRoad = snapshot.CarIdxOnPitRoad;
                var carNumbers = dataProvider.CarNumbers;
                var userNames = dataProvider.UserNames;

                if (carIdxEstTime == null || carIdxLapDistPct == null) return;

                float playerLapDistPct = carIdxLapDistPct[playerCarIdx];

                for (int carIdx = 0; carIdx < carIdxEstTime.Length; carIdx++)
                {
                    if (carNumbers == null || carIdx >= carNumbers.Length || string.IsNullOrEmpty(carNumbers[carIdx]))
                        continue;

                    if (userNames == null || carIdx >= userNames.Length || string.IsNullOrEmpty(userNames[carIdx]))
                        continue;

                    float carLapDistPct = carIdxLapDistPct[carIdx];
                    float directDistance = Math.Abs(carLapDistPct - playerLapDistPct);
                    float proximityDistance = Math.Min(directDistance, 1.0f - directDistance);

                    var row = new StringBuilder();
                    row.Append($"{timestampStr},");
                    row.Append($"{sessionTime:F3},");
                    row.Append($"{playerCarIdx},");
                    row.Append($"{carIdx},");
                    row.Append($"{EscapeCsvField(carNumbers[carIdx])},");
                    row.Append($"{EscapeCsvField(userNames[carIdx])},");
                    row.Append($"{carIdxEstTime[carIdx]:F3},");
                    row.Append($"{carIdxLapDistPct[carIdx]:F6},");
                    row.Append($"{(carIdxLap != null && carIdx < carIdxLap.Length ? carIdxLap[carIdx].ToString() : "")},");
                    row.Append($"{(carIdxLastLapTime != null && carIdx < carIdxLastLapTime.Length ? carIdxLastLapTime[carIdx].ToString("F3") : "")},");
                    row.Append($"{(carIdxOnPitRoad != null && carIdx < carIdxOnPitRoad.Length ? carIdxOnPitRoad[carIdx].ToString() : "")},");
                    row.Append($"{proximityDistance:F6}");

                    _writer.WriteLine(row.ToString());
                }

                _writer.Flush();
            }
            catch (Exception ex)
            {
                Log.Error($"[TelemetryCSV] Error writing data rows: {ex.Message}");
            }
        }

        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return "";

            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }

            return field;
        }

        private string GetSessionNameFromNumber(int sessionNum)
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
            if (_isDisposed)
                return;

            _isDisposed = true;

            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                Log.Info("[TelemetryCSV] Logger disposed");
            }
            catch (Exception ex)
            {
                Log.Error("[TelemetryCSV] Error during disposal", ex);
            }
        }
    }
#endif
}