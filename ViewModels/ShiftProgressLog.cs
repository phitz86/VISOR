using System.Collections.Generic;
using System.Text;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// The text of the shift-point learner's log lines: the once-a-minute progress line, why a
    /// gear's shift point was learned, and car-data changes. Formatting only; the caller gathers
    /// the values under <see cref="ShiftPointProvider"/>'s lock.
    /// </summary>
    internal static class ShiftProgressLog
    {
        /// <summary>
        /// How many frames the learner kept vs skipped (and why), and what each gear is waiting
        /// on. Null while nothing but ineligible frames (garage, pits) arrived, so it stays silent.
        /// </summary>
        public static string? Format(PlayerCarInfo car, long kept, long[] skipped, long sampleCount,
            GearShiftEstimate[] estimates, int[] learned, int[] provisional)
        {
            long active = kept;
            for (int i = 0; i < skipped.Length; i++)
                if (i != (int)SkipReason.Ineligible) active += skipped[i];
            if (active == 0) return null;

            var sb = new StringBuilder();
            sb.Append($"[ShiftPoint] progress ({CarName(car)}): {kept} frames kept, skipped:");
            for (int i = 0; i < skipped.Length; i++)
                if (skipped[i] > 0) sb.Append($" {(SkipReason)i} {skipped[i]},");
            if (sb[^1] == ',') sb.Length--;
            else sb.Append(" none");
            sb.Append(" | total model samples ").Append(sampleCount);

            foreach (var e in estimates)
            {
                sb.Append(" | g").Append(e.Gear).Append(' ');
                if (e.Gear < learned.Length && learned[e.Gear] > 0)
                {
                    sb.Append("learned ").Append(learned[e.Gear]);
                    // Only for holds-to-redline gears, where it says how close the call was.
                    if (e.Reason == "holds to redline" && !double.IsNaN(e.NextGearThrustAtRedlinePct))
                        sb.Append($" (next gear {e.NextGearThrustAtRedlinePct - 100:+0;-0}% at redline)");
                }
                else if (e.Gear < provisional.Length && provisional[e.Gear] > 0)
                {
                    sb.Append($"stepping up {provisional[e.Gear]}");
                    if (e.Provisional) sb.Append($" (proven to {e.ProvenRpm})");
                }
                else if (e.BandsNeeded > 0)
                {
                    sb.Append($"waiting (est {e.Rpm}): {e.BandsSeen}/{e.BandsNeeded} RPM bands seen");
                    if (e.MissingBands.Length > 0) sb.Append($" (missing {e.MissingBands})");
                }
                else sb.Append("waiting: ").Append(e.Reason);
            }
            if (estimates.Length == 0) sb.Append(" | no fit yet");
            return sb.ToString();
        }

        // "holds to redline; at 7500, 5th would pull 4% less" / "crossover; 3rd pulls equal at 7210"
        public static string Explain(GearShiftEstimate e, PlayerCarInfo car)
        {
            string next = Ordinal(e.Gear + 1);
            if (e.Reason == "holds to redline" && !double.IsNaN(e.NextGearThrustAtRedlinePct))
                return $"holds to redline; at {car.RedLine:F0}, {next} would pull {100 - e.NextGearThrustAtRedlinePct:F0}% less";
            if (e.Reason == "crossover")
                return $"crossover; {next} pulls equal at {e.Rpm}";
            return e.Reason;
        }

        private static string Ordinal(int n) => n switch
        {
            1 => "1st",
            2 => "2nd",
            3 => "3rd",
            _ => $"{n}th"
        };

        public static string DescribeCarChanges(PlayerCarInfo a, PlayerCarInfo b)
        {
            var parts = new List<string>();
            void Cmp(string name, float x, float y) { if (x != y) parts.Add($"{name} {x:F0} -> {y:F0}"); }
            Cmp("redline", a.RedLine, b.RedLine);
            Cmp("light first", a.SLFirstRPM, b.SLFirstRPM);
            Cmp("light shift", a.SLShiftRPM, b.SLShiftRPM);
            Cmp("light last", a.SLLastRPM, b.SLLastRPM);
            Cmp("light blink", a.SLBlinkRPM, b.SLBlinkRPM);
            Cmp("gears", a.GearNumForward, b.GearNumForward);
            return string.Join(", ", parts);
        }

        public static string CarName(PlayerCarInfo car) =>
            string.IsNullOrEmpty(car.CarScreenName) ? car.CarPath : car.CarScreenName;
    }
}
