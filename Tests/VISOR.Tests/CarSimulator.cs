using VISOR.Telemetry;

namespace VISOR.Tests
{
    /// <summary>
    /// A simple longitudinal car model that produces the telemetry the shift-point learner sees,
    /// with a known torque curve so the true optimal shift points can be computed analytically.
    /// </summary>
    internal sealed class CarSimulator
    {
        public const float RedLine = 7500f;
        public const double Dt = 1.0 / 60.0;

        private const double Mass = 1250;           // kg
        private const double WheelRadius = 0.33;    // m
        private const double FinalDrive = 4.1;
        private const double Efficiency = 0.9;
        private const double DragCoef = 0.42;       // N per (m/s)^2
        private const double Rolling = 150;         // N
        public static readonly double[] GearRatios = { 3.0, 2.2, 1.7, 1.38, 1.15, 0.98 };

        public int GearCount => GearRatios.Length;

        private readonly Func<double, double> _torque;
        private readonly Random _rng;
        private readonly double _noise;
        private double _time;

        public CarSimulator(int seed = 1, double accelNoise = 0.15, Func<double, double>? torque = null)
        {
            _torque = torque ?? Torque;
            _rng = new Random(seed);
            _noise = accelNoise;
        }

        /// <summary>Engine torque (Nm): peaks at 5500 RPM, 25% down at 3500 and 7500.</summary>
        public static double Torque(double rpm)
        {
            double x = (rpm - 5500) / 4000;
            return 300 * (1 - x * x);
        }

        /// <summary>
        /// A peaky engine whose torque falls off a cliff above 6500 RPM (like a car whose shift
        /// light comes on too late): the right upshift is well short of the redline.
        /// </summary>
        public static double PeakyTorque(double rpm)
        {
            double x = (rpm - 5500) / 4000;
            double baseT = 300 * (1 - x * x);
            return rpm <= 6500 ? baseT : baseT * Math.Max(0.2, 1 - Math.Pow((rpm - 6500) / 1100, 2));
        }

        /// <summary>Overall ratio as RPM per m/s of road speed.</summary>
        public static double K(int gear) => GearRatios[gear - 1] * FinalDrive * 60 / (2 * Math.PI * WheelRadius);

        /// <summary>
        /// True optimal upshift from <paramref name="gear"/>: where wheel thrust in the next gear
        /// overtakes this one at the same road speed, or the redline if it never does.
        /// </summary>
        public static int OptimalShift(int gear) => OptimalShift(gear, Torque);

        public static int OptimalShift(int gear, Func<double, double> torque)
        {
            double kg = K(gear), kn = K(gear + 1), rho = kn / kg;
            for (double r = RedLine / 2; r <= RedLine; r += 1)
                if (kg * torque(r) <= kn * torque(rho * r)) return (int)Math.Round(r);
            return (int)RedLine;
        }

        private double Gaussian()
        {
            double u1 = 1 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        }

        /// <summary>
        /// One full-throttle run from low speed through every gear, then braking back down.
        /// <paramref name="shiftAt"/> picks each upshift RPM (the driver's habit).
        /// <paramref name="wheelspinInFirst"/> makes first gear spin the wheels part of the time.
        /// </summary>
        public IEnumerable<ShiftSample> Run(Func<int, double> shiftAt, bool wheelspinInFirst = false)
        {
            double v = 10;
            int gear = 1;
            double target = shiftAt(gear);  // the driver picks one shift point per gear

            while (true)
            {
                double k = K(gear);
                double rpm = k * v;

                double force = _torque(rpm) * GearRatios[gear - 1] * FinalDrive * Efficiency / WheelRadius;
                double accel = (force - DragCoef * v * v - Rolling) / Mass;

                // Upshift at the driver's chosen RPM, or earlier if the car has stopped pulling.
                if (gear < GearCount && (rpm >= target || accel <= 0.05))
                {
                    // ~0.1 s shift: off throttle, clutch in. The learner must ignore these frames.
                    for (int i = 0; i < 6; i++)
                    {
                        double a = -(DragCoef * v * v + Rolling) / Mass;
                        yield return Frame(gear, k * v, v, a, throttle: 0, clutch: 0);
                        v += a * Dt;
                    }
                    gear++;
                    target = shiftAt(gear);
                    continue;
                }

                // Top gear: lift at the driver's RPM (the redline, unless they short-shift).
                if (gear == GearCount && rpm >= Math.Min(RedLine, target)) break;

                double reportedRpm = rpm;
                if (wheelspinInFirst && gear == 1 && _rng.NextDouble() < 0.3)
                {
                    // Spinning: engine runs ~12% fast for the road speed and the car pulls less.
                    reportedRpm = rpm * 1.12;
                    accel *= 0.6;
                }

                yield return Frame(gear, reportedRpm, v, accel + _noise * Gaussian(), throttle: 1, clutch: 1);
                v += accel * Dt;
                if (accel <= 0.05) break;   // top speed reached
            }

            // Brake back down to 10 m/s, downshifting as we go.
            while (v > 10)
            {
                int g = Math.Max(1, gear);
                yield return Frame(g, K(g) * v, v, -10, throttle: 0, clutch: 1, brake: 1);
                v -= 10 * Dt;
                while (gear > 1 && K(gear) * v < 3000) gear--;
            }
        }

        private ShiftSample Frame(int gear, double rpm, double v, double accel, float throttle, float clutch, float brake = 0)
        {
            var s = new ShiftSample(_time, gear, (float)rpm, (float)v, (float)accel, throttle, brake, clutch, Eligible: true);
            _time += Dt;
            return s;
        }
    }
}
