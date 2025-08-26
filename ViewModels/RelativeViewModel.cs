using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    // Enum to make track surface values readable.
    public enum iRacingTrackSurface
    {
        OnTrack,
        InPitStall,
        NotInWorld = -1
    };

    public class RelativeViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ObservableCollection<RelativeRowViewModel> RelativeRows { get; } = new();

        private string _livePlayerClassPosition = "P--";
        public string LivePlayerClassPosition
        {
            get => _livePlayerClassPosition;
            private set { _livePlayerClassPosition = value; OnPropertyChanged(); }
        }

        private string _livePlayerClassPositionNumber = "--";
        public string LivePlayerClassPositionNumber
        {
            get => _livePlayerClassPositionNumber;
            private set { _livePlayerClassPositionNumber = value; OnPropertyChanged(); }
        }

        // Updated to use integer class IDs and predefined colors
        private readonly Dictionary<int, Brush> _classColorMap = new();
        private readonly Brush[] _classColors = {
            Brushes.Gold,
            Brushes.Silver,
            Brushes.White,
            Brushes.HotPink,
            Brushes.LightBlue
        };
        private int _nextColorIndex = 0;

        public void Update(SVappsLABSnapshot snapshot, VISOR.ViewModels.ISessionDataProvider sessionDataProvider)
        {
            // --- DATA READINESS CHECKS ---
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");
            if (playerCarIdx == -1) return; // Can't do anything if we don't know who the player is.

            // Don't run logic until the session YAML has been parsed.
            if (sessionDataProvider == null || !sessionDataProvider.IsDataReady) return;

            // Get updated data arrays with new field names
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var userNames = sessionDataProvider.UserNames;
            var carNumbers = sessionDataProvider.CarNumbers;
            var carIsAI = sessionDataProvider.CarIsAI;
            var incidentCounts = sessionDataProvider.CurDriverIncidentCount;

            int playerClassID = carClassIDs[playerCarIdx];
            if (playerClassID == 0) return; // Wait until the player's class is known.

            // --- Get all necessary data arrays ---
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
            var playerLastLapTime = snapshot.GetValue<float>("LapLastLapTime");

            if (lapDistPct == null || currentLap == null || trackSurface == null) return;

            var allValidCars = new List<RelativeRowViewModel>();
            for (int i = 0; i < trackSurface.Length; i++)
            {
                // Check if car is valid: on track, has a car number, and has a driver name
                if (trackSurface[i] != (int)iRacingTrackSurface.NotInWorld &&
                    !string.IsNullOrEmpty(carNumbers[i]) &&
                    !string.IsNullOrEmpty(userNames[i]))
                {
                    // Format driver name with AI indicator if needed
                    string displayName = carIsAI[i] ? $"🤖 {userNames[i]}" : userNames[i];

                    allValidCars.Add(new RelativeRowViewModel
                    {
                        CarIdx = i,
                        IsPlayer = (i == playerCarIdx),
                        CurrentLap = currentLap[i],
                        LapDistPct = lapDistPct[i],
                        Name = displayName,
                        CarNum = carNumbers[i],
                        ClassID = carClassIDs[i],
                        IncidentCount = incidentCounts[i]
                    });
                }
            }

            if (!allValidCars.Any()) return;

            // Calculate player's live class position using RACE POSITION sorting (for position display)
            var racePositionSorted = allValidCars.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList();
            var carsInClass = racePositionSorted.Where(c => c.ClassID == playerClassID).ToList();
            if (carsInClass.Any())
            {
                int playerClassPosition = carsInClass.FindIndex(c => c.IsPlayer) + 1;
                if (playerClassPosition > 0)
                {
                    LivePlayerClassPosition = $"P{playerClassPosition}";
                    LivePlayerClassPositionNumber = playerClassPosition.ToString();
                }
            }

            // Use proximity-based display with player hard-coded at center
            var playerRow = allValidCars.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            var finalRows = BuildProximityBasedRows(allValidCars, playerRow, playerLastLapTime);

            // Apply all the coloring, positioning, and gap logic
            ApplyDisplayLogic(finalRows, racePositionSorted, playerLastLapTime);

            UpdateCollection(finalRows);
        }

        /// <summary>
        /// Simple proximity approach: Player is ALWAYS in position 4, find 3 closest cars in each direction
        /// </summary>
        private List<RelativeRowViewModel> BuildProximityBasedRows(List<RelativeRowViewModel> allCars, RelativeRowViewModel playerRow, float playerLastLapTime)
        {
            float playerTrackPercent = playerRow.LapDistPct;

            // Calculate proximity for all non-player cars
            var otherCars = allCars.Where(c => !c.IsPlayer).Select(car =>
            {
                float carTrackPercent = car.LapDistPct;

                // Calculate shortest distance around the track
                float directDistance = Math.Abs(carTrackPercent - playerTrackPercent);
                float wrappedDistance = 1.0f - directDistance;
                float proximity = Math.Min(directDistance, wrappedDistance);

                // Determine if car is "ahead" or "behind" on track
                float rawDifference = carTrackPercent - playerTrackPercent;

                // Handle wrapping for ahead/behind determination
                bool isAhead;
                if (Math.Abs(rawDifference) <= 0.5f)
                {
                    // No wrapping needed
                    isAhead = rawDifference > 0;
                }
                else
                {
                    // Wrapping case - flip the logic
                    isAhead = rawDifference < 0;
                }

                return new { Car = car, Proximity = proximity, IsAhead = isAhead };
            }).ToList();

            // Get cars ahead and behind, sorted by proximity
            var carsAhead = otherCars
                .Where(x => x.IsAhead)
                .OrderBy(x => x.Proximity)
                .Select(x => x.Car)
                .ToList();

            var carsBehind = otherCars
                .Where(x => !x.IsAhead)
                .OrderBy(x => x.Proximity)
                .Select(x => x.Car)
                .ToList();

            // Build the final list with proper filling logic
            var result = new List<RelativeRowViewModel>();

            // Fill 3 slots ahead of player
            for (int i = 0; i < 3; i++)
            {
                if (i < carsAhead.Count)
                {
                    // Add closest cars ahead (in reverse order so closest is last)
                    result.Insert(0, carsAhead[i]);
                }
                else if (carsBehind.Count > 3)
                {
                    // If we don't have enough cars ahead, wrap around and use cars from behind
                    // Skip the first 3 cars behind (they'll be used in the behind section)
                    int wrapIndex = 3 + (i - carsAhead.Count);
                    if (wrapIndex < carsBehind.Count)
                    {
                        result.Insert(0, carsBehind[wrapIndex]);
                    }
                }
            }

            // Add player at center
            result.Add(playerRow);

            // Fill 3 slots behind player
            for (int i = 0; i < 3; i++)
            {
                if (i < carsBehind.Count)
                {
                    result.Add(carsBehind[i]);
                }
                else if (carsAhead.Count > 3)
                {
                    // If we don't have enough cars behind, wrap around and use cars from ahead
                    // Skip the first 3 cars ahead (they'll be used in the ahead section)
                    int wrapIndex = 3 + (i - carsBehind.Count);
                    if (wrapIndex < carsAhead.Count)
                    {
                        result.Add(carsAhead[wrapIndex]);
                    }
                }
            }

            return result;
        }

        private void ApplyDisplayLogic(List<RelativeRowViewModel> displayRows, List<RelativeRowViewModel> racePositionSorted, float playerLastLapTime)
        {
            var playerRow = displayRows.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            int playerDisplayIndex = displayRows.IndexOf(playerRow);
            float playerTrackPercent = playerRow.LapDistPct;

            // Ensure player's class always gets the first color (Gold)
            int playerClassID = playerRow.ClassID;
            if (playerClassID != 0 && !_classColorMap.ContainsKey(playerClassID))
            {
                _classColorMap[playerClassID] = _classColors[0];
                _nextColorIndex = 1;
            }

            // Build class position maps using RACE POSITION sorting
            var classCars = racePositionSorted.GroupBy(c => c.ClassID).ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList()
            );

            foreach (var row in displayRows)
            {
                // Show class position based on race position
                if (classCars.TryGetValue(row.ClassID, out var carsInThisClass))
                {
                    int classPosition = carsInThisClass.IndexOf(row) + 1;
                    row.ClassPos = $"P{classPosition}";
                }
                else
                {
                    row.ClassPos = "P?";
                }

                // Calculate gap based on TRACK PROXIMITY, with proper sign based on display position
                if (playerLastLapTime > 0 && !row.IsPlayer)
                {
                    float carTrackPercent = row.LapDistPct;

                    // Calculate track proximity distance (same logic as proximity sorting)
                    float directDistance = Math.Abs(carTrackPercent - playerTrackPercent);
                    float wrappedDistance = 1.0f - directDistance;
                    float proximityDistance = Math.Min(directDistance, wrappedDistance);

                    // Convert proximity to time gap
                    float timeGap = proximityDistance * playerLastLapTime;

                    int rowDisplayIndex = displayRows.IndexOf(row);

                    if (rowDisplayIndex < playerDisplayIndex)
                    {
                        // Car is ahead of player in display (rows 1-3) = positive gap
                        row.Gap = $"+{timeGap:F1}";
                    }
                    else if (rowDisplayIndex > playerDisplayIndex)
                    {
                        // Car is behind player in display (rows 5-7) = negative gap
                        row.Gap = $"-{timeGap:F1}";
                    }
                    else
                    {
                        row.Gap = "0.0";
                    }
                }

                // Set name color based on lap differential
                row.NameColor = Brushes.White;
                if (row.CurrentLap > playerRow.CurrentLap) row.NameColor = Brushes.Red;
                if (row.CurrentLap < playerRow.CurrentLap) row.NameColor = Brushes.CornflowerBlue;
                if (row.IsPlayer) row.NameColor = Brushes.Yellow;

                // Assign class background color
                if (row.ClassID != 0)
                {
                    if (!_classColorMap.ContainsKey(row.ClassID))
                    {
                        _classColorMap[row.ClassID] = _classColors[_nextColorIndex % _classColors.Length];
                        _nextColorIndex++;
                    }
                    row.ClassBackground = _classColorMap[row.ClassID];
                }
            }
        }

        private void UpdateCollection(List<RelativeRowViewModel> newRows)
        {
            // Update existing rows or add new ones
            for (int i = 0; i < newRows.Count; i++)
            {
                if (i < RelativeRows.Count)
                {
                    var existing = RelativeRows[i];
                    existing.CarIdx = newRows[i].CarIdx;
                    existing.ClassPos = newRows[i].ClassPos;
                    existing.CarNum = newRows[i].CarNum;
                    existing.Name = newRows[i].Name;
                    existing.Gap = newRows[i].Gap;
                    existing.ClassBackground = newRows[i].ClassBackground;
                    existing.NameColor = newRows[i].NameColor;
                    existing.FontStyle = newRows[i].FontStyle;
                    existing.IsPlayer = newRows[i].IsPlayer;
                    existing.ClassID = newRows[i].ClassID;
                    existing.IncidentCount = newRows[i].IncidentCount;
                }
                else
                {
                    RelativeRows.Add(newRows[i]);
                }
            }

            // Remove excess rows if the new list is shorter
            while (RelativeRows.Count > newRows.Count)
            {
                RelativeRows.RemoveAt(RelativeRows.Count - 1);
            }
        }

        public void Reset()
        {
            RelativeRows.Clear();
            _classColorMap.Clear();
            _nextColorIndex = 0;
            LivePlayerClassPosition = "P--";
            LivePlayerClassPositionNumber = "--";
        }
    }
}