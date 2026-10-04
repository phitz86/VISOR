using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using VISOR.Telemetry;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: CSV of the shift-point learner's accepted full-throttle samples and each fit's
    /// per-gear result, for comparing learned shift points across cars.
    /// One file per car per run in %LOCALAPPDATA%\VISOR\Diagnostics\ShiftPoints.
    /// Callers serialize access (ShiftPointProvider calls it under its lock).
    /// </summary>
    public sealed class ShiftPointLogger : IDisposable
    {
        private StreamWriter? _writer;

        public ShiftPointLogger(string carPath)
        {
            try
            {
                string dir = Path.Combine(Log.GetDiagnosticsDirectory(), "ShiftPoints");
                Directory.CreateDirectory(dir);
                string stem = Regex.Replace(carPath ?? "car", "[^A-Za-z0-9_-]", "_");
                string path = Path.Combine(dir, $"{stem}_{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                _writer = new StreamWriter(path) { AutoFlush = false };
                _writer.WriteLine("type,session_time,gear,rpm,speed_mps,long_accel,lat_accel,ratio,est_rpm,confident,reason");
                Log.Info($"[ShiftPointCSV] Output: {path}");
            }
            catch (Exception ex)
            {
                Log.Error($"[ShiftPointCSV] could not open log: {ex.Message}");
                _writer = null;
            }
        }

        public void LogSample(in ShiftSample s, double ratio)
        {
            if (_writer == null) return;
            _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"sample,{s.SessionTime:F3},{s.Gear},{s.Rpm:F0},{s.Speed:F2},{s.LongAccel:F3},{s.LatAccel:F3},{ratio:F2},,,"));
        }

        public void LogEstimate(GearShiftEstimate e)
        {
            if (_writer == null) return;
            _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"fit,,{e.Gear},,,,,,{e.Rpm},{e.Confident},{e.Reason}"));
            _writer.Flush();
        }

        public void Dispose()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* best effort */ }
            _writer = null;
        }
    }
#endif
}
