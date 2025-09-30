using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class RelativeDisplayCalculator
    {
        private const double PROXIMITY_MAX_DISTANCE = 0.15;
        private const double PROXIMITY_ALERT_DISTANCE = 0.05;
        private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#80404040");
        private static readonly Color AheadAlertColor = (Color)ColorConverter.ConvertFromString("#FF00FFFF"); // Teal
        private static readonly Color BehindAlertColor = (Color)ColorConverter.ConvertFromString("#FFFF9900"); // Orange

        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;

        public RelativeDisplayCalculator(Dictionary<int, RelativeRowViewModel> carCache, ClassColorManager classColorManager)
        {
            _carCache = carCache;
            _classColorManager = classColorManager;
        }

        public List<RelativeRowViewModel> Calculate(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");

            var carClassIDs = dataProvider.CarClassIDs;
            var userNames = dataProvider.UserNames;
            var carNumbers = dataProvider.CarNumbers;
            var carIsAI = dataProvider.CarIsAI;
            var incidentCounts = dataProvider.CurDriverIncidentCount;
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
            var onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad");
            var playerLastLapTime = snapshot.GetValue<float>("LapLastLapTime");

            var allValidCars = BuildValidCarsList(trackSurface, carNumbers, userNames, carIsAI,
                carClassIDs, incidentCounts, currentLap, lapDistPct, onPitRoad, playerCarIdx);

            if (!allValidCars.Any())
            {
                return new List<RelativeRowViewModel>();
            }

            List<RelativeRowViewModel> finalRows = BuildProximityBasedRows(allValidCars, playerLastLapTime);

            bool useFastestLap = dataProvider.ShouldUseFastestLapPositioning();
            ApplyDisplayLogic(finalRows, allValidCars, playerLastLapTime, useFastestLap, dataProvider);

            return finalRows;
        }

        public void Reset() { }

        #region Calculation Logic

        private List<RelativeRowViewModel> BuildValidCarsList(int[] trackSurface, string[] carNumbers, string[] userNames,
            bool[] carIsAI, int[] carClassIDs, int[] incidentCounts, int[] currentLap, float[] lapDistPct, bool[] onPitRoad, int playerCarIdx)
        {
            var allValidCars = new List<RelativeRowViewModel>();
            for (int i = 0; i < trackSurface.Length; i++)
            {
                if (trackSurface[i] != (int)iRacingTrackSurface.NotInWorld &&
                    !string.IsNullOrEmpty(carNumbers[i]) &&
                    !string.IsNullOrEmpty(userNames[i]))
                {
                    string displayName = carIsAI[i] ? $"🤖 {userNames[i]}" : userNames[i];
                    if (!_carCache.TryGetValue(i, out var row))
                    {
                        row = new RelativeRowViewModel();
                        _carCache[i] = row;
                    }

                    row.CarIdx = i;
                    row.IsPlayer = (i == playerCarIdx);
                    row.CurrentLap = currentLap[i];
                    row.LapDistPct = lapDistPct[i];
                    row.Name = displayName;
                    row.CarNum = carNumbers[i];
                    row.ClassID = carClassIDs[i];
                    row.IncidentCount = incidentCounts[i];
                    row.IsOnPitRoad = (onPitRoad != null && i < onPitRoad.Length) && onPitRoad[i];
                    allValidCars.Add(row);
                }
            }
            return allValidCars;
        }

        private List<RelativeRowViewModel> BuildProximityBasedRows(List<RelativeRowViewModel> allCars, float playerLastLapTime)
        {
            var playerRow = allCars.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return new List<RelativeRowViewModel>();

            float playerTrackPercent = playerRow.LapDistPct;

            var otherCars = allCars.Where(c => !c.IsPlayer).Select(car =>
            {
                float directDistance = Math.Abs(car.LapDistPct - playerTrackPercent);
                float proximity = Math.Min(directDistance, 1.0f - directDistance);
                bool isAhead = (car.LapDistPct - playerTrackPercent + 1.5f) % 1.0f > 0.5f;
                return new { Car = car, Proximity = proximity, IsAhead = isAhead };
            }).ToList();

            var carsAhead = otherCars.Where(x => x.IsAhead).OrderBy(x => x.Proximity).Select(x => x.Car).ToList();
            var carsBehind = otherCars.Where(x => !x.IsAhead).OrderBy(x => x.Proximity).Select(x => x.Car).ToList();

            var result = new List<RelativeRowViewModel>();
            result.AddRange(carsAhead.Take(3).Reverse());
            result.Add(playerRow);
            result.AddRange(carsBehind.Take(3));
            return result;
        }

        private void ApplyDisplayLogic(List<RelativeRowViewModel> displayRows, List<RelativeRowViewModel> allCars,
            float playerLastLapTime, bool isFastestLapMode, ISessionDataProvider dataProvider)
        {
            var playerRow = displayRows.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            var classPositions = CalculateClassPositions(allCars);
            foreach (var row in displayRows)
            {
                AssignClassPositionDisplay(row, isFastestLapMode, dataProvider, classPositions);
                AssignNameColor(row, playerRow);
                AssignClassBackgroundColor(row, playerRow);
                AssignFontStyle(row);
                AssignProximityBar(row, playerRow);
            }
        }

        private Dictionary<int, Dictionary<int, int>> CalculateClassPositions(List<RelativeRowViewModel> allCars)
        {
            return allCars.GroupBy(c => c.ClassID).ToDictionary(g => g.Key,
                g => g.OrderByDescending(c => c.CurrentLap + c.LapDistPct)
                      .Select((car, index) => new { car.CarIdx, Position = index + 1 })
                      .ToDictionary(x => x.CarIdx, x => x.Position));
        }

        private void AssignClassPositionDisplay(RelativeRowViewModel row, bool isFastestLapMode, ISessionDataProvider dataProvider, Dictionary<int, Dictionary<int, int>> classPositions)
        {
            if (isFastestLapMode)
            {
                var fastestLapData = dataProvider.GetFastestLapPositioning();
                var carData = fastestLapData.FirstOrDefault(d => d.carIdx == row.CarIdx);
                row.ClassPos = (carData.fastestTime > 0) ? $"{carData.position}" : "--";
            }
            else
            {
                row.ClassPos = (classPositions.TryGetValue(row.ClassID, out var positions) && positions.TryGetValue(row.CarIdx, out var classPos)) ? $"{classPos}" : "??";
            }
        }

        private void AssignNameColor(RelativeRowViewModel row, RelativeRowViewModel playerRow)
        {
            if (row.IsPlayer) row.NameColor = Brushes.Yellow;
            else if (row.CurrentLap > playerRow.CurrentLap) row.NameColor = Brushes.Red;
            else if (row.CurrentLap < playerRow.CurrentLap) row.NameColor = Brushes.CornflowerBlue;
            else row.NameColor = Brushes.White;
        }

        private void AssignClassBackgroundColor(RelativeRowViewModel row, RelativeRowViewModel playerRow)
        {
            if (row.ClassID == 0) return;
            row.ClassBackground = _classColorManager.GetClassColor(row.ClassID);
        }

        private void AssignFontStyle(RelativeRowViewModel row)
        {
            row.FontStyle = row.IsOnPitRoad ? FontStyles.Italic : FontStyles.Normal;
        }

        private void AssignProximityBar(RelativeRowViewModel row, RelativeRowViewModel playerRow)
        {
            // Reset defaults
            row.BarWidthRatio = 0.0;
            row.BarStartColor = Colors.Transparent; // <-- CORRECTED
            row.BarEndColor = Colors.Transparent;   // <-- CORRECTED

            if (row.IsPlayer) return;

            float proximityDistance = Math.Min(Math.Abs(row.LapDistPct - playerRow.LapDistPct), 1.0f - Math.Abs(row.LapDistPct - playerRow.LapDistPct));

            if (proximityDistance <= PROXIMITY_MAX_DISTANCE)
            {
                row.BarWidthRatio = 1.0 - (proximityDistance / PROXIMITY_MAX_DISTANCE);

                bool isAhead = (row.LapDistPct - playerRow.LapDistPct + 1.5f) % 1.0f > 0.5f;

                if (proximityDistance > PROXIMITY_ALERT_DISTANCE)
                {
                    row.BarStartColor = NeutralColor;
                    row.BarEndColor = NeutralColor;
                }
                else
                {
                    row.BarStartColor = NeutralColor;
                    row.BarEndColor = isAhead ? AheadAlertColor : BehindAlertColor;
                }
            }
        }
        #endregion
    }
}