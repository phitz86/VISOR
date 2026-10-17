namespace VISOR.Telemetry
{
    /// <summary>
    /// Arithmetic on CarIdxLapDistPct, the fraction of the lap a car has covered past S/F.
    /// </summary>
    public static class LapDistance
    {
        /// <summary>
        /// A real position on the lap. iRacing reports -1 for a car with no telemetry, and a frame
        /// right at S/F can read marginally outside [0, 1].
        /// </summary>
        public static bool IsOnLap(float lapDistPct) => lapDistPct >= 0f && lapDistPct <= 1f;

        /// <summary>
        /// How far <paramref name="toPct"/> is from <paramref name="fromPct"/> the short way round
        /// the lap, in [-0.5, 0.5]: positive when it is ahead.
        /// </summary>
        public static float Delta(float fromPct, float toPct)
        {
            float delta = toPct - fromPct;
            if (delta > 0.5f) return delta - 1f;
            if (delta < -0.5f) return delta + 1f;
            return delta;
        }
    }
}
