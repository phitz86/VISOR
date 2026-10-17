namespace VISOR.Telemetry
{
    /// <summary>
    /// One telemetry frame as the shift-point learner sees it. Plain values only, so the learner
    /// has no dependency on the SDK or WPF and can be unit-tested with synthetic data.
    /// </summary>
    public readonly record struct ShiftSample(
        double SessionTime,     // s; used to detect gear changes and skip the post-shift transient
        int Gear,               // -1 R, 0 N, 1..n
        float Rpm,
        float Speed,            // m/s
        float LongAccel,        // m/s^2, including gravity
        float LatAccel,         // m/s^2; hard cornering is excluded from the torque fit
        float Throttle,         // 0..1
        float Brake,            // 0..1
        float Clutch,           // 0..1, 1 = fully engaged
        bool Eligible);         // caller's gate: on track, not in pits/replay/limiter, track dry

    /// <summary>
    /// Result of solving for one gear's optimal upshift.
    /// </summary>
    /// <remarks>
    /// <see cref="BandsSeen"/> / <see cref="BandsNeeded"/>: how many of the 250-RPM torque bands
    /// around the shift point (in this gear and where the next gear lands) have enough data yet.
    /// <see cref="NextGearThrustAtRedlinePct"/>: the next gear's thrust as a % of this gear's when
    /// shifting at the redline (same road speed). Below 100 means holding to the redline beats
    /// shifting there; the further below, the more clear-cut. Only meaningful near 100 (for gears
    /// that hold to the redline); far above it for a peaky engine it's noisy and irrelevant, since
    /// the shift then happens well before the redline. NaN when not computed.
    /// <see cref="Provisional"/> / <see cref="ProvenRpm"/>: not confident yet, but the data proves
    /// this gear still out-pulls the next up to <see cref="ProvenRpm"/>, so the best shift is at
    /// least that high. <see cref="Rpm"/> is then one step (250 RPM) past it, capped at the
    /// model's estimate: where to cue next so the driver revs high enough to confirm the step.
    /// </remarks>
    public readonly record struct GearShiftEstimate(int Gear, int Rpm, bool Confident, string Reason,
        int BandsSeen = 0, int BandsNeeded = 0, string MissingBands = "", double NextGearThrustAtRedlinePct = double.NaN,
        bool Provisional = false, int ProvenRpm = 0);

    /// <summary>Why a frame was left out of the torque model (for progress logging).</summary>
    public enum SkipReason
    {
        Ineligible,     // caller's gate: pits, off track, replay, limiter, wet
        NotInGear,
        PostShift,
        LowSpeed,
        ClutchOrBrake,
        PartThrottle,
        Cornering,
        RatioWarmup,    // gear ratio not measured yet this session
        Wheelspin,
        OutOfRange
    }

    /// <summary>The persisted torque statistics of a <see cref="ShiftPointLearner"/>.</summary>
    /// <remarks>
    /// <see cref="GearRatios"/> (index = gear, 0 = unknown) are optional: older saved models don't
    /// have them. They're only ever used as a fallback; see <see cref="ShiftPointLearner.GetRatio"/>.
    /// </remarks>
    public sealed record ShiftModelState(double[] AtaUpper, double[] Atb, double[] BinWeights, long SampleCount,
        double[]? GearRatios = null);
}
