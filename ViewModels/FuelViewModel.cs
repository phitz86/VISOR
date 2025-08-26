using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Diagnostics;

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
            //Debug.WriteLine($"[Fuel Debug] Raw fuelLevel={fuelLevel:F4}, currentLap={currentLap}");

            if (currentLap > _lastProcessedLap)
            {
                _fuelLevelAtLapEnd[currentLap] = fuelLevel;
                //Debug.WriteLine($"[Fuel Debug] Lap {currentLap} completed, fuel recorded: {fuelLevel:F4}");

                if (_fuelLevelAtLapEnd.TryGetValue(currentLap - 1, out float previousFuelLevel))
                {
                    float fuelUsedLastLap = previousFuelLevel - fuelLevel;
                    //Debug.WriteLine($"[Fuel Debug] Lap {currentLap}: Previous={previousFuelLevel:F4}, Current={fuelLevel:F4}, Used={fuelUsedLastLap:F4}");

                    if (fuelUsedLastLap > 0.001f)
                    {
                        _lastLapsFuelUsage.Add(fuelUsedLastLap);
                        //Debug.WriteLine($"[Fuel Debug] Added fuel usage: {fuelUsedLastLap:F4}, Total samples: {_lastLapsFuelUsage.Count}");

                        if (_lastLapsFuelUsage.Count > MovingAverageLapCount)
                        {
                            _lastLapsFuelUsage.RemoveAt(0);
                        }
                    }
                    else
                    {
                        //Debug.WriteLine($"[Fuel Debug] Fuel usage too small ({fuelUsedLastLap:F4}), ignoring");
                    }
                }
                _lastProcessedLap = currentLap;
            }

            if (_lastLapsFuelUsage.Any())
            {
                float averageFuelPerLap = _lastLapsFuelUsage.Average();
                //Debug.WriteLine($"[Fuel Debug] Average fuel per lap: {averageFuelPerLap:F4}");

                if (averageFuelPerLap > 0.001f)
                {
                    float lapsRemaining = fuelLevel / averageFuelPerLap;
                    //Debug.WriteLine($"[Fuel Debug] Calculation: {fuelLevel:F4} / {averageFuelPerLap:F4} = {lapsRemaining:F1} laps");
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
            Debug.WriteLine("[Fuel Debug] Reset called");
        }
    }
}