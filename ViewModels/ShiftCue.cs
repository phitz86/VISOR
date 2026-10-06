using System;

namespace VISOR.ViewModels
{
    /// <summary>
    /// What the ⚙ gear symbol shows.
    /// </summary>
    public enum ShiftState
    {
        Normal,     // light gray
        Approach,   // solid amber: shift point coming up
        ShiftNow,   // flashing white/red: shift now
        Limiter     // solid red: at redline / on the rev limiter
    }

    /// <summary>
    /// Turns RPM into the gear symbol's state, frame by frame.
    ///
    /// The cue leads the shift point by a reaction time: amber and the flash trigger when the RPM
    /// *projected* <see cref="ReactionLeadSeconds"/> ahead (from how fast RPM is climbing)
    /// reaches their thresholds. In low gears, where RPM climbs fastest, that brings the cue
    /// noticeably earlier; in top gear it barely moves. The limiter state uses actual RPM.
    ///
    /// Plain numbers in, state out: no WPF or SDK dependency, so it's unit-tested directly.
    /// </summary>
    public sealed class ShiftCue
    {
        public const double ReactionLeadSeconds = 0.15;

        // Once a state is reached it holds until RPM falls this far below its threshold (or the
        // gear changes), so RPM hovering at a threshold can't flicker it.
        public const int HysteresisRpm = 150;

        private const double RateSmoothing = 0.3;   // exponential average weight per frame (~3-frame time constant at 60 Hz)
        private const double MaxFrameGap = 0.1;     // s; longer gaps (pauses, reconnects) restart the rate
        private const double StaleWindow = 1.0;     // s; a slightly older frame is stale, a much older one is a session reset

        private int _gear = int.MinValue;
        private double _lastTime = double.NaN;
        private float _lastRpm;
        private double _rate;

        public ShiftState State { get; private set; } = ShiftState.Normal;

        /// <summary>Smoothed RPM change per second in the current gear.</summary>
        public double RpmRate => _rate;

        /// <param name="active">Indicator on and applicable (setting on, on track, not in pits or on the pit limiter).</param>
        /// <param name="shiftRpm">Shift point for this gear; 0 when there's nothing to shift to (top gear).</param>
        /// <param name="approachRpm">Where amber starts.</param>
        public ShiftState Update(double sessionTime, int gear, float rpm, bool active, bool revLimiterActive,
            float redLine, int shiftRpm, int approachRpm)
        {
            // Frames can arrive slightly out of order (they're fanned out on worker tasks); a
            // frame a little older than the last one is stale and ignored. A much older one means
            // the session clock restarted.
            if (!double.IsNaN(_lastTime) && sessionTime <= _lastTime && sessionTime > _lastTime - StaleWindow)
                return State;

            bool sameGear = gear == _gear;
            if (!sameGear || double.IsNaN(_lastTime))
            {
                _rate = 0;
            }
            else
            {
                double dt = sessionTime - _lastTime;
                if (dt > 0 && dt <= MaxFrameGap)
                    _rate += RateSmoothing * ((rpm - _lastRpm) / dt - _rate);
                else
                    _rate = 0;
            }

            _gear = gear;
            _lastTime = sessionTime;
            _lastRpm = rpm;

            var next = active && gear >= 1
                ? Compute(rpm, sameGear, revLimiterActive, redLine, shiftRpm, approachRpm)
                : ShiftState.Normal;
            State = next;
            return next;
        }

        public void Reset()
        {
            _gear = int.MinValue;
            _lastTime = double.NaN;
            _rate = 0;
            State = ShiftState.Normal;
        }

        private ShiftState Compute(float rpm, bool sameGear, bool revLimiterActive, float redLine, int shiftRpm, int approachRpm)
        {
            // A state already showing in this gear stays until RPM drops past the hysteresis band.
            float Hold(ShiftState state) => sameGear && State >= state ? HysteresisRpm : 0;

            if (revLimiterActive || (redLine > 0 && rpm >= redLine - Hold(ShiftState.Limiter)))
                return ShiftState.Limiter;

            // Top gear (shift RPM 0) only ever shows the limiter state.
            if (shiftRpm <= 0) return ShiftState.Normal;

            double projected = rpm + Math.Max(_rate, 0) * ReactionLeadSeconds;

            if (projected >= shiftRpm - Hold(ShiftState.ShiftNow))
                return ShiftState.ShiftNow;

            if (approachRpm > 0 && projected >= approachRpm - Hold(ShiftState.Approach))
                return ShiftState.Approach;

            return ShiftState.Normal;
        }
    }
}
