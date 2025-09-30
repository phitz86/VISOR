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

        private const int MovingAverageLapCount = 3;
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

        public void Update(float fuelLevel, int currentLap)
        {
            if (currentLap > _lastProcessedLap)
            {
                _fuelLevelAtLapEnd[currentLap] = fuelLevel;

                if (_fuelLevelAtLapEnd.TryGetValue(currentLap - 1, out float previousFuelLevel))
                {
                    float fuelUsedLastLap = previousFuelLevel - fuelLevel;

                    if (fuelUsedLastLap > 0.001f)
                    {
                        _lastLapsFuelUsage.Add(fuelUsedLastLap);

                        if (_lastLapsFuelUsage.Count > MovingAverageLapCount)
                        {
                            _lastLapsFuelUsage.RemoveAt(0);
                        }
                    }
                }
                _lastProcessedLap = currentLap;
            }

            if (_lastLapsFuelUsage.Any())
            {
                float averageFuelPerLap = _lastLapsFuelUsage.Average();

                if (averageFuelPerLap > 0.001f)
                {
                    float lapsRemaining = fuelLevel / averageFuelPerLap;
                    FuelDisplay = $"{lapsRemaining:F1} Laps";
                }
            }
            else
            {
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