using System.Windows.Data;
using System.Globalization;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class DeltaBarViewModel : ObservableObject
    {
        private const float MaxDeltaSeconds = 3.0f;

        private double _leftBarWidth = 0.0;
        private double _rightBarWidth = 0.0;

        public double LeftBarWidth
        {
            get => _leftBarWidth;
            private set => Set(ref _leftBarWidth, value);
        }

        public double RightBarWidth
        {
            get => _rightBarWidth;
            private set => Set(ref _rightBarWidth, value);
        }

        /// <summary>Sets the bars from the lap delta to the session best, capped at ±3 s.</summary>
        public void Update(SVappsLABSnapshot snapshot)
        {
            // Positive = slower than session best, negative = faster.
            float deltaToSessionBest = snapshot.LapDeltaToSessionBestLap;

            float clampedDelta = Math.Max(-MaxDeltaSeconds, Math.Min(MaxDeltaSeconds, deltaToSessionBest));

            if (clampedDelta < 0)
            {
                LeftBarWidth = 0.0;
                RightBarWidth = Math.Abs(clampedDelta) / MaxDeltaSeconds * 100.0;
            }
            else if (clampedDelta > 0)
            {
                LeftBarWidth = clampedDelta / MaxDeltaSeconds * 100.0;
                RightBarWidth = 0.0;
            }
            else
            {
                LeftBarWidth = 0.0;
                RightBarWidth = 0.0;
            }
        }

        /// <summary>
        /// Reset the delta bar to neutral state
        /// </summary>
        public void Reset()
        {
            LeftBarWidth = 0.0;
            RightBarWidth = 0.0;
        }
    }

    /// <summary>
    /// Converts a percentage value (0-100) to actual width based on parent container width
    /// Used specifically for the delta bar center-out design
    /// </summary>
    public class DeltaBarWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length != 2) return 0.0;

            // values[0] = percentage 0-100, values[1] = container width.
            if (values[0] is double percentage && values[1] is double containerWidth)
            {
                // Center-out design uses half the container width as the max extent.
                double halfWidth = containerWidth / 2.0;
                return percentage / 100.0 * halfWidth;
            }

            return 0.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}