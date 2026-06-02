using System;
using System.Collections.Generic;
using SVappsLAB.iRacingTelemetrySDK;

namespace VISOR.Telemetry
{
    public class SVappsLABSnapshot
    {
        public DateTime Timestamp { get; init; }
        public bool IsValid { get; init; }

        // Legacy dictionary path. Retained during the Option Y typed-snapshot migration so
        // consumers that still call GetValue<T> keep working while we migrate them one class
        // at a time. Removed in the final cleanup step once nothing reads it.
        public Dictionary<string, object> RawTelemetryData { get; init; } = new();

        // Typed live-telemetry source (Option Y). The accessors below read from this and
        // mirror TelemetryDataBuilder's null/default handling exactly, so a typed read is
        // value-identical to the GetValue<T> dict read it replaces.
        public TelemetryData Data { get; init; }

        public SVappsLABSnapshot(Dictionary<string, object> telemetry, TelemetryData data, DateTime timestamp)
        {
            Timestamp = timestamp;
            IsValid = telemetry != null;
            RawTelemetryData = telemetry ?? new();
            Data = data;
        }

        #region Typed accessors (Option Y)

        // Shared, length-64 empty arrays so a null SDK array normalizes the same way
        // TelemetryDataBuilder.SetArray did (null -> new T[64]). Consumers only read these.
        private static readonly float[] EmptyFloat64 = new float[64];
        private static readonly int[] EmptyInt64 = new int[64];
        private static readonly bool[] EmptyBool64 = new bool[64];

        // --- Scalars ---
        // PlayerCarIdx uses the -1 "no player" sentinel, matching the builder's default.
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

        // SessionState is a nullable enum under SDK 1.2.1. The builder casts it straight to int
        // (and throws on null, caught upstream -> consumers then see their -1 default); we make
        // that explicit and null-safe here while preserving the -1 "unknown" fallback.
        public int SessionState => (int?)Data.SessionState ?? -1;

        // SessionFlags is an enum (flags); the builder normalizes it via Convert.ToInt32.
        public int SessionFlags => Convert.ToInt32(Data.SessionFlags);

        // --- Per-car arrays (padded to length 64 on null, as the builder did) ---
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

        // Deferred to the Radar/Position migration step (SDK enum-typed):
        //   CarIdxTrackSurface is TrackLocation[]; CarLeftRight is an enum.
        // Note: today's dict stores CarIdxTrackSurface as TrackLocation[], so consumers reading
        // GetValue<int[]>("CarIdxTrackSurface") currently get null (type mismatch). Going typed
        // will hand them real data, which is a behavior change to verify, not a blind swap.

        #endregion

        /// <summary>
        /// A robust method to get a value from the telemetry data, handling type conversions safely.
        /// </summary>
        public T GetValue<T>(string fieldName, T defaultValue = default)
        {
            if (RawTelemetryData == null || !RawTelemetryData.TryGetValue(fieldName, out var value))
                return defaultValue;

            if (value is T tVal) return tVal;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
