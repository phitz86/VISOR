using System;
using System.IO;
using System.Text;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: Logs the relative gap calculation pipeline for cars in the 7-row display.
    /// Captures geometry, gap source (buffer/stationary/pit/none), the assigned positions, and the
    /// final display values. Caller gates writes by proximity (only near-side-by-side cars), so this
    /// logs at full frame rate during a battle and stays silent otherwise — small files, all the
    /// flicker frames captured.
    /// </summary>
    public class RelativeGapLogger : IDisposable
    {
        private readonly string _outputDirectory;
        private StreamWriter? _writer;
        private bool _isDisposed = false;
        private bool _headerWritten = false;

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
        /// Call once per frame. Writes the header on the first frame; per-row gating is the caller's
        /// job (it logs only cars within a close proximity window).
        /// </summary>
        public void BeginFrame()
        {
            if (_isDisposed || _writer == null) return;

            if (!_headerWritten)
            {
                WriteHeader();
                _headerWritten = true;
            }
        }

        /// <summary>
        /// Log one row of the relative display pipeline. Call from AssignProximitySegments only for
        /// cars that pass the proximity gate. ClassPos/OverallPos/TrackPos expose the position-number
        /// sort so a number swap can be seen alongside the relative-slot decision.
        /// </summary>
        public void LogRow(
            float sessionTime,
            int slotIndex,
            string carNum,
            float playerLapDistPct,
            float oppLapDistPct,
            float distDelta,
            bool isAhead,
            string gapSource,
            float displayGap,
            string gapText,
            int classPos,
            int overallPos,
            float trackPosition)
        {
            if (_isDisposed || _writer == null) return;

            try
            {
                var sb = new StringBuilder();
                sb.Append($"{sessionTime:F2},");
                sb.Append($"{slotIndex},");
                sb.Append($"{carNum},");
                sb.Append($"{playerLapDistPct:F4},");
                sb.Append($"{oppLapDistPct:F4},");
                sb.Append($"{distDelta:F4},");
                sb.Append($"{isAhead},");
                sb.Append($"{gapSource},");
                sb.Append($"{displayGap:F2},");
                sb.Append($"{classPos},");
                sb.Append($"{overallPos},");
                sb.Append($"{trackPosition:F4},");
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
            header.Append("IsAhead,");
            header.Append("GapSource,");
            header.Append("DisplayGap,");
            header.Append("ClassPos,");
            header.Append("OverallPos,");
            header.Append("TrackPos,");
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
