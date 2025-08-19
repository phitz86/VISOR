using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VISOR.ViewModels
{
    public class FuelViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private const int MovingAverageLapCount = 3; // Use a 3-lap moving average for stability
        private readonly List<float> _lastLapsFuelUsage = new();
        private readonly Dictionary<int, float> _fuelLevelAtLapEnd = new();
        private int _lastProcessedLap = -1;

        private string _fuelDisplay = "--- Laps";
        public string FuelDisplay
        {
            get => _fuelDisplay;
            private set
            {
                if (_fuelDisplay != value)
                {
                    _fuelDisplay = value;
                    OnPropertyChanged();
                }
            }
        }
        /// Updates the fuel calculation using a robust, historical method.
        /// <param name="fuelLevel">Current fuel in the tank.</param>
        /// <param name="currentLap">The current lap number ("Laps completed").</param>
        public void Update(float fuelLevel, int currentLap)
        {
            // We only trigger logic when the completed lap number changes.
            if (currentLap > _lastProcessedLap)
            {
                // 1. Record the fuel level at the exact moment this lap completed.
                _fuelLevelAtLapEnd[currentLap] = fuelLevel;

                // 2. Check if we have the data from the *previous* lap to perform a calculation.
                //    This implicitly ignores the out-lap.
                if (_fuelLevelAtLapEnd.TryGetValue(currentLap - 1, out float previousFuelLevel))
                {
                    // 3. Calculate the precise fuel used over the lap that just finished.
                    float fuelUsedLastLap = previousFuelLevel - fuelLevel;

                    // 4. Sanity check: Ensure consumption is a realistic positive number.
                    if (fuelUsedLastLap > 0.1f)
                    {
                        _lastLapsFuelUsage.Add(fuelUsedLastLap);
                        // Keep the moving average list trimmed to the last 3 laps.
                        if (_lastLapsFuelUsage.Count > MovingAverageLapCount)
                        {
                            _lastLapsFuelUsage.RemoveAt(0);
                        }
                    }
                }
                // Mark this lap as processed so we don't run the logic again until the next lap.
                _lastProcessedLap = currentLap;
            }

            // On every telemetry tick, update the display with the latest calculation.
            if (_lastLapsFuelUsage.Any())
            {
                float averageFuelPerLap = _lastLapsFuelUsage.Average();
                if (averageFuelPerLap > 0.1f)
                {
                    // Use the most up-to-date fuelLevel for the most accurate remaining laps.
                    float lapsRemaining = fuelLevel / averageFuelPerLap;
                    FuelDisplay = $"{lapsRemaining:F1} Laps";
                }
            }
            else
            {
                // If we don't have enough data yet, show the default text.
                FuelDisplay = "--- Laps";
            }
        }
        public void Reset()
        {
            _lastLapsFuelUsage.Clear();
            _fuelLevelAtLapEnd.Clear();
            _lastProcessedLap = -1;
            FuelDisplay = "--- Laps";
        }
    }
}
