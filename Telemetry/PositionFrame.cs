using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// The telemetry PositionCalculator reads each frame, as plain values. Built from the SDK
    /// snapshot by SVappsLABSnapshot.ToPositionFrame; this file has no SDK types in it, so the
    /// calculator can be unit-tested with scripted frames.
    ///
    /// Every per-car array is <see cref="CarCount"/> long: the calculator indexes all 64 car
    /// slots without bounds checks.
    /// </summary>
    public sealed record PositionFrame
    {
        public const int CarCount = 64;

        // --- What the positions are computed from ---
        public required int SessionNum { get; init; }
        public required int SessionState { get; init; }
        public required float[] CarIdxLapDistPct { get; init; }
        public required int[] CarIdxLap { get; init; }
        public required int[] CarIdxLapCompleted { get; init; }
        public required bool[] CarIdxOnPitRoad { get; init; }
        public required int[] CarIdxClassPosition { get; init; }
        public required int[] CarIdxPosition { get; init; }

        // --- Only logged (finish and telemetry-gap diagnostics) ---
        public required int SessionFlags { get; init; }
        public required int PlayerCarIdx { get; init; }
        public required int[] CarIdxTrackSurface { get; init; }
        public required float[] CarIdxBestLapTime { get; init; }
        public required float[] CarIdxEstTime { get; init; }

        /// <summary>
        /// The array itself when it already covers every car slot (iRacing's always do), otherwise
        /// a <see cref="CarCount"/>-long copy padded with <paramref name="missing"/>.
        /// </summary>
        public static T[] AllCars<T>(T[]? values, T missing)
        {
            if (values != null && values.Length >= CarCount)
                return values;

            var padded = new T[CarCount];
            Array.Fill(padded, missing);
            if (values != null)
                Array.Copy(values, padded, values.Length);
            return padded;
        }
    }
}
