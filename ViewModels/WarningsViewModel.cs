using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace VISOR.ViewModels
{
    public class WarningsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private int _incidentCount = 0;
        private string _incidentDisplay = "0x";
        private Brush _incidentColor = Brushes.White;

        private readonly List<float> _rollingPaceLapTimes = new List<float>();
        private readonly List<float> _bestPaceLapTimes = new List<float>();
        private const int PaceLapWindow = 4;
        private bool _isPaceWarningVisible = false;
        private bool _isPersistentDotVisible = false;
        private bool _isPitNowVisible = false;

        public string IncidentDisplay { get => _incidentDisplay; private set { _incidentDisplay = value; OnPropertyChanged(); } }
        public Brush IncidentColor { get => _incidentColor; private set { _incidentColor = value; OnPropertyChanged(); } }
        public bool IsPaceWarningVisible { get => _isPaceWarningVisible; private set { _isPaceWarningVisible = value; OnPropertyChanged(); } }
        public bool IsPersistentDotVisible { get => _isPersistentDotVisible; private set { _isPersistentDotVisible = value; OnPropertyChanged(); } }
        public bool IsPitNowVisible { get => _isPitNowVisible; private set { _isPitNowVisible = value; OnPropertyChanged(); } }
        public string PitNowText => "PIT";

        public void UpdateIncidentCount(int newCount, int incidentLimit)
        {
            if (newCount != _incidentCount)
            {
                _incidentCount = newCount;
                IncidentDisplay = $"{newCount}x";
                IncidentColor = GetIncidentColor(newCount);
            }
        }

        public void CheckPace(float lastLapTime, float lastLapTopSpeed, float topSpeedBaseline, int lapsRemaining, bool isOnPitRoad)
        {
            if (lastLapTime <= 0 || isOnPitRoad || _rollingPaceLapTimes.Count < 2)
            {
                if (lastLapTime > 0 && !isOnPitRoad) _rollingPaceLapTimes.Add(lastLapTime);
                ClearPaceWarnings();
                return;
            }

            _rollingPaceLapTimes.Add(lastLapTime);
            while (_rollingPaceLapTimes.Count > PaceLapWindow) _rollingPaceLapTimes.RemoveAt(0);

            if (!_bestPaceLapTimes.Any() || lastLapTime < _bestPaceLapTimes.Min() * 1.02f)
            {
                _bestPaceLapTimes.Add(lastLapTime);
            }

            float rollingAverage = _rollingPaceLapTimes.Take(_rollingPaceLapTimes.Count - 1).Average();
            float bestPaceAverage = _bestPaceLapTimes.Average();

            bool isTier2Active = (lastLapTopSpeed < (topSpeedBaseline * 0.96f)) && (lastLapTime > (rollingAverage * 1.04f));

            IsPaceWarningVisible = isTier2Active;
            if (isTier2Active) IsPersistentDotVisible = true;

            if (isTier2Active && lapsRemaining > 0)
            {
                float timeLostPerLap = lastLapTime - rollingAverage;
                float paceLossPercent = (lastLapTime / rollingAverage) - 1.0f;
                float dynamicRepairEstimate = CalculateDynamicRepairTime(paceLossPercent);
                float totalPitTime = 35.0f + dynamicRepairEstimate;

                if ((timeLostPerLap * lapsRemaining) > totalPitTime)
                {
                    IsPitNowVisible = true;
                }
                else
                {
                    IsPitNowVisible = false;
                }
            }
            else
            {
                IsPitNowVisible = false;
            }

            if (IsPersistentDotVisible && lastLapTime < (bestPaceAverage * 1.01f))
            {
                IsPersistentDotVisible = false;
            }
        }

        private float CalculateDynamicRepairTime(float paceLossPercent)
        {
            if (paceLossPercent <= 0.04f) return 60f;
            if (paceLossPercent <= 0.08f) return 150f;
            return 300f;
        }

        private void ClearPaceWarnings()
        {
            IsPaceWarningVisible = false;
            IsPitNowVisible = false;
        }

        private Brush GetIncidentColor(int count)
        {
            if (count == 0) return Brushes.White;
            if (count <= 4) return Brushes.Yellow;
            if (count <= 8) return Brushes.Orange;
            return Brushes.Red;
        }

        public void Reset()
        {
            _incidentCount = 0;
            IncidentDisplay = "0x";
            IncidentColor = Brushes.White;
            _rollingPaceLapTimes.Clear();
            _bestPaceLapTimes.Clear();
            ClearPaceWarnings();
            IsPersistentDotVisible = false;
        }
    }
}