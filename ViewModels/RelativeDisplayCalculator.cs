using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Performs all complex calculations for the relative drivers display.
    /// This class is stateful and should be instantiated once per RelativeViewModel.
    /// </summary>
    public class RelativeDisplayCalculator
    {
        private readonly Dictionary<int, RelativeRowViewModel> _carCache;
        private readonly ClassColorManager _classColorManager;

        public RelativeDisplayCalculator(Dictionary<int, RelativeRowViewModel> carCache, ClassColorManager classColorManager)
        {
            _carCache = carCache;
            _classColorManager = classColorManager;
        }

        public (List<RelativeRowViewModel> Rows, string PlayerPos, string PlayerPosNum) Calculate(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");

            // --- Get all data arrays from snapshot and provider ---
            var carClassIDs = dataProvider.CarClassIDs;
            var userNames = dataProvider.UserNames;
            var carNumbers = dataProvider.CarNumbers;
            var carIsAI = dataProvider.CarIsAI;
            var incidentCounts = dataProvider.CurDriverIncidentCount;
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
            var onPitRoad = snapshot.GetValue<bool[]>("CarIdxOnPitRoad"); // Get pit road data
            var playerLastLapTime = snapshot.GetValue<float>("LapLastLapTime");

            var allValidCars = BuildValidCarsList(trackSurface, carNumbers, userNames, carIsAI,
                carClassIDs, incidentCounts, currentLap, lapDistPct, onPitRoad, playerCarIdx); // Pass pit road data

            if (!allValidCars.Any())
            {
                return (new List<RelativeRowViewModel>(), "P--", "--");
            }

            List<RelativeRowViewModel> finalRows;
            bool useFastestLap = dataProvider.ShouldUseFastestLapPositioning();

            // ALWAYS build the list of cars shown in the relative display based on physical proximity on track.
            finalRows = BuildProximityBasedRows(allValidCars, playerLastLapTime);

            var playerPos = CalculatePlayerClassPosition(allValidCars, playerCarIdx, useFastestLap, dataProvider);
            ApplyDisplayLogic(finalRows, allValidCars, playerLastLapTime, useFastestLap, dataProvider);

            return (finalRows, playerPos.PlayerPos, playerPos.PlayerPosNum);
        }

        public void Reset()
        {
            // Note: ClassColorManager reset is handled by MainViewModel
            // No need to reset shared service here since other components may still be using it
        }

        #region Calculation Logic (Moved from RelativeViewModel)

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
                    row.IsOnPitRoad = (onPitRoad != null && i < onPitRoad.Length) && onPitRoad[i]; // Set pit road status
                    allValidCars.Add(row);
                }
            }
            return allValidCars;
        }

        private List<RelativeRowViewModel> BuildFastestLapBasedRows(List<RelativeRowViewModel> allCars,
            int playerCarIdx, ISessionDataProvider sessionDataProvider)
        {
            var playerRow = allCars.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return new List<RelativeRowViewModel>();

            var fastestLapData = sessionDataProvider.GetFastestLapPositioning();
            var allCarsWithPositions = new List<(RelativeRowViewModel car, int position)>();

            if (fastestLapData.Any())
            {
                var fastestLapLookup = fastestLapData.ToDictionary(x => x.carIdx, x => x.position);
                foreach (var car in allCars)
                {
                    int pos = fastestLapLookup.TryGetValue(car.CarIdx, out var p) ? p : 999 + car.CarIdx;
                    allCarsWithPositions.Add((car, pos));
                }
            }
            else
            {
                foreach (var car in allCars) allCarsWithPositions.Add((car, 999 + car.CarIdx));
            }

            var sortedByFastestLap = allCarsWithPositions.OrderBy(x => x.position).Select(x => x.car).ToList();
            int playerFastestLapIndex = sortedByFastestLap.FindIndex(c => c.IsPlayer);
            if (playerFastestLapIndex == -1) return new List<RelativeRowViewModel> { playerRow };

            var result = new List<RelativeRowViewModel>();
            for (int i = Math.Max(0, playerFastestLapIndex - 3); i < playerFastestLapIndex; i++) result.Add(sortedByFastestLap[i]);
            result.Add(playerRow);
            for (int i = playerFastestLapIndex + 1; i < Math.Min(sortedByFastestLap.Count, playerFastestLapIndex + 4); i++) result.Add(sortedByFastestLap[i]);
            return result;
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

        private (string PlayerPos, string PlayerPosNum) CalculatePlayerClassPosition(List<RelativeRowViewModel> allCars, int playerCarIdx, bool isFastestLapMode, ISessionDataProvider dataProvider)
        {
            var player = allCars.FirstOrDefault(c => c.CarIdx == playerCarIdx);
            if (player == null) return ("P--", "--");

            var carsInClass = allCars.Where(c => c.ClassID == player.ClassID).ToList();

            if (isFastestLapMode)
            {
                var fastestLaps = dataProvider.GetFastestLapPositioning().ToDictionary(d => d.carIdx, d => d.position);
                var sorted = carsInClass.OrderBy(c => fastestLaps.GetValueOrDefault(c.CarIdx, 999)).ToList();
                int pos = sorted.FindIndex(c => c.IsPlayer) + 1;
                return pos > 0 ? ($"P{pos}", pos.ToString()) : ("P--", "--");
            }
            else
            {
                var sorted = carsInClass.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList();
                int pos = sorted.FindIndex(c => c.IsPlayer) + 1;
                return pos > 0 ? ($"P{pos}", pos.ToString()) : ("P--", "--");
            }
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
                AssignGapDisplay(row, playerRow, displayRows, playerLastLapTime, isFastestLapMode);
                AssignNameColor(row, playerRow);
                AssignClassBackgroundColor(row, playerRow);
                AssignFontStyle(row);
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
                row.ClassPos = (carData.fastestTime > 0) ? $"P{carData.position}" : "P--";
            }
            else
            {
                row.ClassPos = (classPositions.TryGetValue(row.ClassID, out var positions) && positions.TryGetValue(row.CarIdx, out var classPos)) ? $"P{classPos}" : "P??";
            }
        }

        private void AssignGapDisplay(RelativeRowViewModel row, RelativeRowViewModel playerRow, List<RelativeRowViewModel> displayRows, float playerLastLapTime, bool isFastestLapMode)
        {
            if (row.IsPlayer)
            {
                row.Gap = "0.0";
                return;
            }

            if (playerLastLapTime <= 0)
            {
                row.Gap = "0.0";
                return;
            }

            // This logic now runs for all session types.
            float proximityDistance = Math.Min(Math.Abs(row.LapDistPct - playerRow.LapDistPct), 1.0f - Math.Abs(row.LapDistPct - playerRow.LapDistPct));
            float rawTimeGap = proximityDistance * playerLastLapTime;
            row.UpdateSmoothedGap(rawTimeGap);

            int playerDisplayIndex = displayRows.IndexOf(playerRow);
            int rowDisplayIndex = displayRows.IndexOf(row);
            string sign = (rowDisplayIndex < playerDisplayIndex) ? "+" : "-";

            row.Gap = $"{sign}{row.SmoothedGap:F1}";
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

            // Use the shared ClassColorManager for consistent colors
            row.ClassBackground = _classColorManager.GetClassColor(row.ClassID);
        }

        private void AssignFontStyle(RelativeRowViewModel row)
        {
            row.FontStyle = row.IsOnPitRoad ? FontStyles.Italic : FontStyles.Normal;
        }

        #endregion
    }
}