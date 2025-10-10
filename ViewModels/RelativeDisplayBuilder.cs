using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Builds the 7-row proximity-based relative display with visual properties.
    /// Uses PositionCalculator's valid car roster as the starting point for filtering.
    /// Focuses on proximity sorting and visual styling - no position calculation.
    /// </summary>
    public class RelativeDisplayBuilder
    {
        #region Constants
        private const double PROXIMITY_MAX_DISTANCE = 0.15;
        private const double PROXIMITY_ALERT_DISTANCE = 0.05;
        private static readonly Color NeutralColor = (Color)ColorConverter.ConvertFromString("#80404040");
        private static readonly Color AheadAlertColor = (Color)ColorConverter.ConvertFromString("#FF00FFFF");
        private static readonly Color BehindAlertColor = (Color)ColorConverter.ConvertFromString("#FFFF9900");
        #endregion

        #region Private Fields
        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;
        private readonly PositionCalculator _positionCalculator;
        #endregion

        #region Constructor
        public RelativeDisplayBuilder(
            Dictionary<int, RelativeRowViewModel> carCache,
            ClassColorManager classColorManager,
            PositionCalculator positionCalculator)
        {
            _carCache = carCache;
            _classColorManager = classColorManager;
            _positionCalculator = positionCalculator;
        }
        #endregion

        #region Public Methods
        /// <summary>
        /// Calculate the 7-row proximity display (3 ahead, player, 3 behind).
        /// Uses valid cars from PositionCalculator and sorts by proximity to player.
        /// </summary>
        public List<RelativeRowViewModel> Calculate(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");

            var carClassIDs = dataProvider.CarClassIDs;
            var carClassColors = dataProvider.CarClassColors;
            var userNames = dataProvider.UserNames;
            var carNumbers = dataProvider.CarNumbers;
            var carIsAI = dataProvider.CarIsAI;
            var incidentCounts = dataProvider.CurDriverIncidentCount;
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad");

            // Get valid cars from PositionCalculator
            var validCarIndices = _positionCalculator.ValidCarIndices;

            var allValidCars = BuildValidCarsList(validCarIndices, carNumbers, userNames, carIsAI,
                carClassIDs, incidentCounts, currentLap, lapDistPct, onPitRoad, playerCarIdx);

            if (!allValidCars.Any())
            {
                return new List<RelativeRowViewModel>();
            }

            List<RelativeRowViewModel> finalRows = BuildProximityBasedRows(allValidCars);

            bool useFastestLap = dataProvider.ShouldUseFastestLapPositioning();
            ApplyDisplayLogic(finalRows, useFastestLap, dataProvider, carClassColors, carClassIDs);

            return finalRows;
        }

        public void Reset() { }
        #endregion

        #region Private Methods - Car List Building
        private List<RelativeRowViewModel> BuildValidCarsList(
            IReadOnlySet<int> validCarIndices,
            string[] carNumbers,
            string[] userNames,
            bool[] carIsAI,
            int[] carClassIDs,
            int[] incidentCounts,
            int[] currentLap,
            float[] lapDistPct,
            bool[] onPitRoad,
            int playerCarIdx)
        {
            var allValidCars = new List<RelativeRowViewModel>();

            // Only include cars from PositionCalculator's valid car roster
            foreach (int i in validCarIndices)
            {
                if (i >= 0 && i < carNumbers.Length &&
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
        #endregion

        #region Private Methods - Proximity Sorting
        private List<RelativeRowViewModel> BuildProximityBasedRows(List<RelativeRowViewModel> allCars)
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
        #endregion

        #region Private Methods - Display Styling
        private void ApplyDisplayLogic(
            List<RelativeRowViewModel> displayRows,
            bool isFastestLapMode,
            ISessionDataProvider dataProvider,
            int[] carClassColors,
            int[] carClassIDs)
        {
            var playerRow = displayRows.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            foreach (var row in displayRows)
            {
                AssignClassPositionDisplay(row, isFastestLapMode, dataProvider);
                AssignNameColor(row, playerRow);
                AssignClassBackgroundColor(row, playerRow, carClassColors, carClassIDs);
                AssignFontStyle(row);
                AssignProximityBar(row, playerRow);
            }
        }

        private void AssignClassPositionDisplay(
            RelativeRowViewModel row,
            bool isFastestLapMode,
            ISessionDataProvider dataProvider)
        {
            if (isFastestLapMode)
            {
                var fastestLapData = dataProvider.GetFastestLapPositioning();
                var carData = fastestLapData.FirstOrDefault(d => d.carIdx == row.CarIdx);
                row.ClassPos = (carData.fastestTime > 0) ? $"{carData.position}" : "--";
            }
            else
            {
                int position = _positionCalculator.GetClassPosition(row.CarIdx, row.ClassID);
                row.ClassPos = (position > 0) ? $"{position}" : "--";
            }
        }

        private void AssignNameColor(RelativeRowViewModel row, RelativeRowViewModel playerRow)
        {
            if (row.IsPlayer)
                row.NameColor = Brushes.Yellow;
            else if (row.CurrentLap > playerRow.CurrentLap)
                row.NameColor = Brushes.Red;
            else if (row.CurrentLap < playerRow.CurrentLap)
                row.NameColor = Brushes.CornflowerBlue;
            else
                row.NameColor = Brushes.White;
        }

        private void AssignClassBackgroundColor(
            RelativeRowViewModel row,
            RelativeRowViewModel playerRow,
            int[] carClassColors,
            int[] carClassIDs)
        {
            if (row.ClassID == 0) return;
            row.ClassBackground = _classColorManager.GetClassColor(row.ClassID, carClassColors, carClassIDs);
        }

        private void AssignFontStyle(RelativeRowViewModel row)
        {
            row.FontStyle = row.IsOnPitRoad ? FontStyles.Italic : FontStyles.Normal;
        }

        private void AssignProximityBar(RelativeRowViewModel row, RelativeRowViewModel playerRow)
        {
            row.BarWidthRatio = 0.0;
            row.BarStartColor = Colors.Transparent;
            row.BarEndColor = Colors.Transparent;

            if (row.IsPlayer) return;

            float proximityDistance = Math.Min(
                Math.Abs(row.LapDistPct - playerRow.LapDistPct),
                1.0f - Math.Abs(row.LapDistPct - playerRow.LapDistPct));

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