using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using VISOR.Telemetry;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: raw 60 Hz telemetry from wet-track running, for researching a grip-aware wet
    /// shift model. Records every throttle level (wet driving is mostly part-throttle, which the
    /// shift learner ignores) and where on track each sample was taken: iRacing's TrackWetness is
    /// one value for the whole track, but real grip varies corner to corner and line to line.
    /// One file per car per run in %LOCALAPPDATA%\VISOR\Diagnostics\WetResearch.
    /// Nullable columns are blank when the live feed doesn't provide that variable.
    /// Callers serialize access (ShiftPointProvider calls it under its lock).
    /// </summary>
    public sealed class WetResearchLogger : IDisposable
    {
        private const int MinWetness = 2;   // 2 = mostly dry; 3+ increasingly wet
        private readonly string _carPath;
        private StreamWriter? _writer;
        private bool _failed;
        private int _sinceFlush;

        public WetResearchLogger(string carPath)
        {
            _carPath = carPath ?? "car";
        }

        public static bool ShouldLog(SVappsLABSnapshot s, bool onPitRoad) =>
            s.IsOnTrack && !s.IsReplayPlaying && !onPitRoad
            && (s.TrackWetness >= MinWetness || s.WeatherDeclaredWet == true);

        public void Write(SVappsLABSnapshot s, double gearRatio)
        {
            if (!EnsureOpen()) return;

            int idx = s.PlayerCarIdx;
            int lap = idx >= 0 && idx < s.CarIdxLap.Length ? s.CarIdxLap[idx] : -1;
            float lapPct = idx >= 0 && idx < s.CarIdxLapDistPct.Length ? s.CarIdxLapDistPct[idx] : -1f;

            // Wheelspin measure: how far RPM/speed sits off the gear's measured ratio.
            string ratioDev = gearRatio > 0 && s.Speed > 1f
                ? (s.RPM / s.Speed / gearRatio - 1).ToString("F4", CultureInfo.InvariantCulture)
                : string.Empty;

            _writer!.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{s.SessionTime:F3},{lap},{lapPct:F5},{N(s.Lat, "F7")},{N(s.Lon, "F7")}," +
                $"{s.TrackWetness},{N(s.Precipitation, "F3")},{N(s.WeatherDeclaredWet)},{N(s.PlayerTireCompound)}," +
                $"{s.Gear},{s.RPM:F0},{s.Speed:F2},{s.Throttle:F3},{s.Brake:F3},{s.Clutch:F3},{N(s.SteeringWheelAngle, "F4")}," +
                $"{s.LongAccel:F3},{s.LatAccel:F3},{N(s.YawRate, "F4")}," +
                $"{ratioDev},{N(s.LFspeed, "F2")},{N(s.RFspeed, "F2")},{N(s.LRspeed, "F2")},{N(s.RRspeed, "F2")}"));

            if (++_sinceFlush >= 300) { _writer.Flush(); _sinceFlush = 0; }   // ~5 s
        }

        private static string N(float? v, string fmt) => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : string.Empty;
        private static string N(double? v, string fmt) => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : string.Empty;
        private static string N(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        private static string N(bool? v) => v.HasValue ? (v.Value ? "1" : "0") : string.Empty;

        // Opened lazily on the first wet frame, so dry sessions leave no empty files behind.
        private bool EnsureOpen()
        {
            if (_writer != null) return true;
            if (_failed) return false;
            try
            {
                string dir = Path.Combine(Log.GetDiagnosticsDirectory(), "WetResearch");
                Directory.CreateDirectory(dir);
                string stem = Regex.Replace(_carPath, "[^A-Za-z0-9_-]", "_");
                string path = Path.Combine(dir, $"{stem}_{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                _writer = new StreamWriter(path) { AutoFlush = false };
                _writer.WriteLine("session_time,lap,lap_dist_pct,lat,lon," +
                                  "track_wetness,precipitation,declared_wet,tire_compound," +
                                  "gear,rpm,speed_mps,throttle,brake,clutch,steering_rad," +
                                  "long_accel,lat_accel,yaw_rate," +
                                  "ratio_dev,lf_speed,rf_speed,lr_speed,rr_speed");
                Log.Info($"[WetResearchCSV] Output: {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[WetResearchCSV] could not open log: {ex.Message}");
                _failed = true;
                return false;
            }
        }

        public void Dispose()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { /* best effort */ }
            _writer = null;
        }
    }
#endif
}
