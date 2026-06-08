using System;
using System.IO;
using System.Text;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: Logs the relative gap calculation pipeline for cars in the 7-row display.
    /// Captures geometry, gap source (buffer/stationary/pit/none), and final display values.
    /// Samples at 1Hz, logging only the cars actively shown in the relative display.
    /// </summary>
    public class RelativeGapLogger : IDisposable
    {
        private readonly string _outputDirectory;
        private StreamWriter? _writer;
        private DateTime _lastLogTime = DateTime.MinValue;
        private readonly TimeSpan _logInterval = TimeSpan.FromSeconds(1.0);
        private bool _isDisposed = false;
        private bool _headerWritten = false;
        private bool _shouldLogThisFrame = false;

        public RelativeGapLogger()
        {
            _outputDirectory = Path.Combine(Log.GetDiagnosticsDirectory(), "RelativeGap");
            Directory.CreateDirectory(_outputDirectory);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string filename = $"RelativeGap_{timestamp}.csv";
            string path = Path.Combine(_outputDirectory, filename);

            try
            {
                _writer = new StreamWriter(path, false, Encoding.UTF8);
                Log.Info($"[RelativeGapLog] Started: {filename}");
            }
            catch (Exception ex)
            {
                Log.Error($"[RelativeGapLog] Failed to create log file: {ex.Message}");
            }
        }

        /// <summary>
        /// Call once per frame to determine if this frame should be logged (1Hz throttle).
        /// </summary>
        public void BeginFrame()
        {
            if (_isDisposed || _writer == null) return;

            DateTime now = DateTime.UtcNow;
            _shouldLogThisFrame = (now - _lastLogTime >= _logInterval);
            if (_shouldLogThisFrame)
            {
                _lastLogTime = now;

                if (!_headerWritten)
                {
                    WriteHeader();
                    _headerWritten = true;
                }
            }
        }

        /// <summary>
        /// Log one row of the relative display pipeline. Call from AssignProximitySegments.
        /// </summary>
        public void LogRow(
            float sessionTime,
            int slotIndex,
            string carNum,
            float playerLapDistPct,
            float oppLapDistPct,
            float distDelta,
            bool isGeometricallyAhead,
            string gapSource,
            float displayGap,
            string gapText)
        {
            if (!_shouldLogThisFrame || _writer == null) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append($"{sessionTime:F2},");
                sb.Append($"{slotIndex},");
                sb.Append($"{carNum},");
                sb.Append($"{playerLapDistPct:F4},");
                sb.Append($"{oppLapDistPct:F4},");
                sb.Append($"{distDelta:F4},");
                sb.Append($"{isGeometricallyAhead},");
                sb.Append($"{gapSource},");
                sb.Append($"{displayGap:F2},");
                sb.Append($"{gapText}");

                _writer.WriteLine(sb.ToString());
                _writer.Flush();
            }
            catch (Exception ex)
            {
                Log.Error($"[RelativeGapLog] Error writing row: {ex.Message}");
            }
        }

        private void WriteHeader()
        {
            if (_writer == null) return;
            var header = new StringBuilder();
            header.Append("SessionTime,");
            header.Append("SlotIndex,");
            header.Append("CarNum,");
            header.Append("PlayerLapDistPct,");
            header.Append("OppLapDistPct,");
            header.Append("DistDelta,");
            header.Append("IsGeomAhead,");
            header.Append("GapSource,");
            header.Append("DisplayGap,");
            header.Append("GapText");

            _writer.WriteLine(header.ToString());
            _writer.Flush();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                Log.Info("[RelativeGapLog] Logger disposed");
            }
            catch (Exception ex)
            {
                Log.Error($"[RelativeGapLog] Error during disposal: {ex.Message}");
            }
        }
    }
#endif
}
