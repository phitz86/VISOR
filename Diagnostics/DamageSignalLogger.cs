using System;
using System.Globalization;
using System.IO;
using System.Text;
using VISOR.Telemetry;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: temporary diagnostic to determine whether iRacing's repair-time telemetry
    /// (PitRepairLeft / PitOptRepairLeft) is readable while the car is on track, or whether it
    /// only populates in the pits. Logs the player's damage-related scalars alongside pit state
    /// so the timeline can be correlated against pit road / pit stall entry.
    ///
    /// Samples at 1Hz, plus an immediate row whenever any damage signal or pit state changes,
    /// so transitions are captured precisely. Remove once the question is answered.
    /// </summary>
    public class DamageSignalLogger : IDisposable
    {
        private readonly string _outputDirectory;
        private StreamWriter _writer;
        private DateTime _lastLogTime = DateTime.MinValue;
        private readonly TimeSpan _logInterval = TimeSpan.FromSeconds(1.0);
        private int _lastSessionNum = -1;
        private bool _isDisposed = false;

        // Previous values, for change-triggered logging.
        private float _prevRepair = float.NaN;
        private float _prevOptRepair = float.NaN;
        private float _prevTow = float.NaN;
        private bool _prevOnPitRoad = false;
        private bool _prevInPitStall = false;
        private bool _hasPrev = false;

        public DamageSignalLogger()
        {
            _outputDirectory = Path.Combine(Log.GetDiagnosticsDirectory(), "DamageSignals");
            Directory.CreateDirectory(_outputDirectory);
            Log.Info($"[DamageSignal] Output directory: {_outputDirectory}");
        }

        public void LogSnapshot(SVappsLABSnapshot snapshot)
        {
            if (_isDisposed || snapshot == null || !snapshot.IsValid)
                return;

            try
            {
                int sessionNum = snapshot.SessionNum;
                if (sessionNum != _lastSessionNum)
                {
                    StartNewSessionFile(sessionNum);
                    _lastSessionNum = sessionNum;
                }

                if (_writer == null)
                    return;

                int playerIdx = snapshot.PlayerCarIdx;
                bool onPitRoad = playerIdx >= 0
                    && snapshot.CarIdxOnPitRoad != null
                    && playerIdx < snapshot.CarIdxOnPitRoad.Length
                    && snapshot.CarIdxOnPitRoad[playerIdx];

                float repair = snapshot.PitRepairLeft;
                float optRepair = snapshot.PitOptRepairLeft;
                float tow = snapshot.PlayerCarTowTime;
                bool inPitStall = snapshot.PlayerCarInPitStall;

                bool changed = !_hasPrev
                    || !NearlyEqual(repair, _prevRepair)
                    || !NearlyEqual(optRepair, _prevOptRepair)
                    || !NearlyEqual(tow, _prevTow)
                    || onPitRoad != _prevOnPitRoad
                    || inPitStall != _prevInPitStall;

                DateTime now = DateTime.UtcNow;
                bool intervalElapsed = now - _lastLogTime >= _logInterval;

                if (!changed && !intervalElapsed)
                    return;

                _lastLogTime = now;
                _prevRepair = repair;
                _prevOptRepair = optRepair;
                _prevTow = tow;
                _prevOnPitRoad = onPitRoad;
                _prevInPitStall = inPitStall;
                _hasPrev = true;

                WriteRow(now, snapshot, onPitRoad, inPitStall, repair, optRepair, tow, changed);
            }
            catch (Exception ex)
            {
                Log.Error($"[DamageSignal] Error logging snapshot: {ex.Message}");
            }
        }

        private void WriteRow(DateTime now, SVappsLABSnapshot snapshot, bool onPitRoad,
            bool inPitStall, float repair, float optRepair, float tow, bool changeTriggered)
        {
            var row = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            row.Append(now.ToString("yyyyMMdd HH:mm:ss.fff", ci)).Append(',');
            row.Append(snapshot.SessionTime.ToString("F3", ci)).Append(',');
            row.Append(snapshot.SessionState.ToString(ci)).Append(',');
            row.Append(onPitRoad ? "1" : "0").Append(',');
            row.Append(inPitStall ? "1" : "0").Append(',');
            row.Append(snapshot.Speed.ToString("F1", ci)).Append(',');
            row.Append(snapshot.LapLastLapTime.ToString("F3", ci)).Append(',');
            row.Append(repair.ToString("F2", ci)).Append(',');
            row.Append(optRepair.ToString("F2", ci)).Append(',');
            row.Append(tow.ToString("F2", ci)).Append(',');
            row.Append(changeTriggered ? "change" : "interval");

            _writer.WriteLine(row.ToString());
            _writer.Flush();
        }

        private void StartNewSessionFile(int sessionNum)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _writer = null;
                _hasPrev = false;

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filename = $"DamageSignals_Session{sessionNum}_{timestamp}.csv";
                string path = Path.Combine(_outputDirectory, filename);

                _writer = new StreamWriter(path, false, Encoding.UTF8);
                _writer.WriteLine("Timestamp,SessionTime,SessionState,OnPitRoad,InPitStall,Speed,LapLastLapTime,PitRepairLeft,PitOptRepairLeft,PlayerCarTowTime,Trigger");
                _writer.Flush();
                Log.Info($"[DamageSignal] Started new log file: {filename}");
            }
            catch (Exception ex)
            {
                Log.Error("[DamageSignal] Failed to start new session file", ex);
            }
        }

        private static bool NearlyEqual(float a, float b)
        {
            if (float.IsNaN(a) || float.IsNaN(b))
                return false;
            return Math.Abs(a - b) < 0.05f;
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
                Log.Info("[DamageSignal] Logger disposed");
            }
            catch (Exception ex)
            {
                Log.Error("[DamageSignal] Error during disposal", ex);
            }
        }
    }
#endif
}
