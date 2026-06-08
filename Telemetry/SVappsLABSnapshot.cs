using System;
using SVappsLAB.iRacingTelemetrySDK;

namespace VISOR.Telemetry
{
    public class SVappsLABSnapshot
    {
        public DateTime Timestamp { get; init; }
        public bool IsValid { get; init; }

        // Strongly-typed live telemetry from the SDK's source generator. All reads go through the
        // typed accessors below, which normalize nulls/defaults centrally.
        public TelemetryData Data { get; init; }

        public SVappsLABSnapshot(TelemetryData data, DateTime timestamp)
        {
            Timestamp = timestamp;
            // The wrapper only constructs a snapshot from a delivered telemetry frame, so it is
            // always usable. (The former dict was likewise always non-null, i.e. IsValid==true.)
            IsValid = true;
            Data = data;
        }

        #region Typed accessors

        // Shared, length-64 empty arrays so a null SDK array normalizes to a fixed-size,
        // non-null array (iRacing addresses 64 car slots). Consumers only read these.
        private static readonly float[] EmptyFloat64 = new float[64];
        private static readonly int[] EmptyInt64 = new int[64];
        private static readonly bool[] EmptyBool64 = new bool[64];

        // --- Scalars ---
        // PlayerCarIdx uses the -1 "no player" sentinel.
        public int PlayerCarIdx => Data.PlayerCarIdx ?? -1;

        public float FuelLevel => Data.FuelLevel ?? 0f;
        public float FuelUsePerHour => Data.FuelUsePerHour ?? 0f;
        public int Lap => Data.Lap ?? 0;
        public int Gear => Data.Gear ?? 0;
        public float Speed => Data.Speed ?? 0f;
        public float RPM => Data.RPM ?? 0f;

        public float LapCurrentLapTime => Data.LapCurrentLapTime ?? 0f;
        public float LapLastLapTime => Data.LapLastLapTime ?? 0f;
        public float LapBestLapTime => Data.LapBestLapTime ?? 0f;
        public float LapDeltaToBestLap => Data.LapDeltaToBestLap ?? 0f;
        public float LapDeltaToOptimalLap => Data.LapDeltaToOptimalLap ?? 0f;
        public float LapDeltaToSessionBestLap => Data.LapDeltaToSessionBestLap ?? 0f;

        public double SessionTime => Data.SessionTime ?? 0.0;
        public double SessionTimeRemain => Data.SessionTimeRemain ?? 0.0;
        public int SessionLapsRemain => Data.SessionLapsRemain ?? 0;
        public int SessionLapsTotal => Data.SessionLapsTotal ?? 0;
        public int SessionNum => Data.SessionNum ?? 0;

        // SessionState is a nullable enum under SDK 1.2.1; cast to int with a -1 "unknown"
        // fallback so the accessor is null-safe and never throws.
        public int SessionState => (int?)Data.SessionState ?? -1;

        // SessionFlags is an enum (flags); normalize to its underlying int.
        public int SessionFlags => Convert.ToInt32(Data.SessionFlags);

        // --- Per-car arrays (normalized to length 64 on null) ---
        public float[] CarIdxLapDistPct => Data.CarIdxLapDistPct ?? EmptyFloat64;
        public int[] CarIdxLap => Data.CarIdxLap ?? EmptyInt64;
        public int[] CarIdxLapCompleted => Data.CarIdxLapCompleted ?? EmptyInt64;
        public bool[] CarIdxOnPitRoad => Data.CarIdxOnPitRoad ?? EmptyBool64;
        public float[] CarIdxLastLapTime => Data.CarIdxLastLapTime ?? EmptyFloat64;
        public float[] CarIdxBestLapTime => Data.CarIdxBestLapTime ?? EmptyFloat64;
        public float[] CarIdxEstTime => Data.CarIdxEstTime ?? EmptyFloat64;
        public float[] CarIdxF2Time => Data.CarIdxF2Time ?? EmptyFloat64;
        public int[] CarIdxPosition => Data.CarIdxPosition ?? EmptyInt64;
        public int[] CarIdxClassPosition => Data.CarIdxClassPosition ?? EmptyInt64;

        // SDK enum-typed fields, resolved without naming the SDK enum types directly.
        // CarIdxTrackSurface is TrackLocation[]; the CLR lets an enum array be read through an
        // int[] reference (same underlying storage), so expose it as int[] for zero consumer
        // change. CarLeftRight is a nullable enum; both radar consumers only use its name string
        // (compared against "CarLeft"/"CarRight"/"Clear"/"Off"/...), so expose .ToString()
        // directly — dropping the boxing + object-cast the dict path required.
        public int[] CarIdxTrackSurface => (int[])(object)Data.CarIdxTrackSurface ?? EmptyInt64;
        public string CarLeftRightState => Data.CarLeftRight?.ToString() ?? "Off";

        #endregion
    }
}
