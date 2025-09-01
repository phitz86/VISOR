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

        // Debug throttling
        private int _debugCounter = 0;
        private bool _lastUsedFastestLap = false;
        private int _lastPlayerPosition = -1;
        private int _lastValidCarCount = -1;

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

        // Track which cars we've seen before to manage smoothing
        private readonly Dictionary<int, RelativeRowViewModel> _carCache = new();

        public void Update(SVappsLABSnapshot snapshot, VISOR.ViewModels.ISessionDataProvider sessionDataProvider)
        {
            // Throttled debug - only every 60th frame (~once per second at 60fps)
            _debugCounter++;
            bool shouldDebug = _debugCounter % 60 == 0;

            if (shouldDebug) System.Diagnostics.Debug.WriteLine("[RelativeVM] Update() called");

            // --- DATA READINESS CHECKS ---
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");
            if (playerCarIdx == -1)
            {
                if (shouldDebug) System.Diagnostics.Debug.WriteLine("[RelativeVM] PlayerCarIdx is -1, returning early");
                return; // Can't do anything if we don't know who the player is.
            }

            // Don't run logic until the session YAML has been parsed.
            if (sessionDataProvider == null || !sessionDataProvider.IsDataReady)
            {
                if (shouldDebug) System.Diagnostics.Debug.WriteLine($"[RelativeVM] Session data not ready - provider null: {sessionDataProvider == null}, data ready: {sessionDataProvider?.IsDataReady}");
                return;
            }

            // NEW: Session-aware positioning logic
            var sdkWrapper = sessionDataProvider as SVappsLABSDKWrapper;
            if (shouldDebug) System.Diagnostics.Debug.WriteLine($"[RelativeVM] SDK wrapper cast successful: {sdkWrapper != null}");

            // Check if we should hide relative display entirely (lone qualifying)
            bool shouldHide = false;
            try
            {
                shouldHide = sdkWrapper?.ShouldHideRelativeDisplay() == true;
                if (shouldDebug) System.Diagnostics.Debug.WriteLine($"[RelativeVM] ShouldHideRelativeDisplay returned: {shouldHide}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RelativeVM] Exception in ShouldHideRelativeDisplay: {ex.Message}");
            }

            if (shouldHide)
            {
                if (shouldDebug) System.Diagnostics.Debug.WriteLine("[RelativeVM] Lone qualifying detected - hiding relative display");
                RelativeRows.Clear();
                LivePlayerClassPosition = "P--";
                LivePlayerClassPositionNumber = "--";
                return;
            }

            // Get updated data arrays
            var carClassIDs = sessionDataProvider.CarClassIDs;
            var userNames = sessionDataProvider.UserNames;
            var carNumbers = sessionDataProvider.CarNumbers;
            var carIsAI = sessionDataProvider.CarIsAI;
            var incidentCounts = sessionDataProvider.CurDriverIncidentCount;

            if (shouldDebug) System.Diagnostics.Debug.WriteLine($"[RelativeVM] Retrieved session data arrays - userNames count: {userNames?.Length ?? 0}");

            int playerClassID = carClassIDs[playerCarIdx];
            if (shouldDebug)
            {
                System.Diagnostics.Debug.WriteLine($"[RelativeVM] PlayerCarIdx: {playerCarIdx}, PlayerClassID: {playerClassID}");

                // Debug: Show first few class IDs to see what we're getting (only on debug frames)
                for (int i = 0; i < Math.Min(3, carClassIDs.Length); i++)
                {
                    System.Diagnostics.Debug.WriteLine($"[RelativeVM] CarIdx {i}: ClassID = {carClassIDs[i]}, UserName = '{userNames[i]}'");
                }
            }

            // FIXED: In single-class sessions, class ID 0 is valid. Only reject if player has no name (not loaded yet)
            if (string.IsNullOrEmpty(userNames[playerCarIdx]))
            {
                if (shouldDebug) System.Diagnostics.Debug.WriteLine("[RelativeVM] Player has no user name yet, returning early");
                return; // Wait until the player's data is loaded
            }

            if (shouldDebug) System.Diagnostics.Debug.WriteLine($"[RelativeVM] Player class ID: {playerClassID}");

            // Get all necessary data arrays
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
            var playerLastLapTime = snapshot.GetValue<float>("LapLastLapTime");

            if (lapDistPct == null || currentLap == null || trackSurface == null)
            {
                if (shouldDebug) System.Diagnostics.Debug.WriteLine("[RelativeVM] Missing telemetry arrays, returning early");
                return;
            }

            if (shouldDebug) System.Diagnostics.Debug.WriteLine($"[RelativeVM] Got telemetry arrays - trackSurface length: {trackSurface.Length}");

            var allValidCars = BuildValidCarsList(trackSurface, carNumbers, userNames, carIsAI,
                carClassIDs, incidentCounts, currentLap, lapDistPct, playerCarIdx);

            if (!allValidCars.Any()) return;

            // NEW: Context-aware positioning logic
            List<RelativeRowViewModel> finalRows;
            bool useFastestLap = sdkWrapper?.ShouldUseFastestLapPositioning() == true;

            // Only debug when positioning mode changes
            if (useFastestLap != _lastUsedFastestLap)
            {
                System.Diagnostics.Debug.WriteLine($"[RelativeVM] Positioning mode changed to: {(useFastestLap ? "Fastest Lap" : "Race Position")}");
                _lastUsedFastestLap = useFastestLap;
            }

            if (useFastestLap)
            {
                finalRows = BuildFastestLapBasedRows(allValidCars, playerCarIdx, sdkWrapper);
            }
            else
            {
                finalRows = BuildProximityBasedRows(allValidCars, playerCarIdx, playerLastLapTime);
            }

            // Calculate player's live class position (context-aware)
            CalculatePlayerClassPosition(allValidCars, playerClassID, playerCarIdx, sdkWrapper?.ShouldUseFastestLapPositioning() == true, sdkWrapper);

            // Apply display logic (coloring, gaps, etc.)
            ApplyDisplayLogic(finalRows, allValidCars, playerLastLapTime, sdkWrapper?.ShouldUseFastestLapPositioning() == true, sdkWrapper);

            UpdateCollection(finalRows);
        }

        private List<RelativeRowViewModel> BuildValidCarsList(int[] trackSurface, string[] carNumbers, string[] userNames,
            bool[] carIsAI, int[] carClassIDs, int[] incidentCounts, int[] currentLap, float[] lapDistPct, int playerCarIdx)
        {
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

                    RelativeRowViewModel row;

                    // Check if we've seen this car before and reuse its smoothing state
                    if (_carCache.ContainsKey(i))
                    {
                        row = _carCache[i];
                        // Update the row with new data
                        row.CarIdx = i;
                        row.IsPlayer = (i == playerCarIdx);
                        row.CurrentLap = currentLap[i];
                        row.LapDistPct = lapDistPct[i];
                        row.Name = displayName;
                        row.CarNum = carNumbers[i];
                        row.ClassID = carClassIDs[i];
                        row.IncidentCount = incidentCounts[i];
                    }
                    else
                    {
                        // New car - create fresh row
                        row = new RelativeRowViewModel
                        {
                            CarIdx = i,
                            IsPlayer = (i == playerCarIdx),
                            CurrentLap = currentLap[i],
                            LapDistPct = lapDistPct[i],
                            Name = displayName,
                            CarNum = carNumbers[i],
                            ClassID = carClassIDs[i],
                            IncidentCount = incidentCounts[i]
                        };
                        _carCache[i] = row;
                    }

                    allValidCars.Add(row);
                }
            }

            return allValidCars;
        }

        /// <summary>
        /// NEW: Build relative display based on fastest lap times for practice/qualifying
        /// </summary>
        private List<RelativeRowViewModel> BuildFastestLapBasedRows(List<RelativeRowViewModel> allCars,
            int playerCarIdx, SVappsLABSDKWrapper sdkWrapper)
        {
            var playerRow = allCars.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return new List<RelativeRowViewModel>();

            // Get fastest lap positioning data from the wrapper
            var fastestLapData = sdkWrapper.GetFastestLapPositioning();

            // Create position list including cars without lap times
            var allCarsWithPositions = new List<(RelativeRowViewModel car, int position, bool hasValidTime)>();

            if (fastestLapData.Any())
            {
                // Cars with fastest lap times get their actual positions
                var fastestLapLookup = fastestLapData.ToDictionary(x => x.carIdx, x => new { x.position, x.fastestTime });

                foreach (var car in allCars)
                {
                    if (fastestLapLookup.ContainsKey(car.CarIdx) && fastestLapLookup[car.CarIdx].fastestTime > 0)
                    {
                        // Has valid fastest lap time
                        allCarsWithPositions.Add((car, fastestLapLookup[car.CarIdx].position, true));
                    }
                    else
                    {
                        // No valid fastest lap time - goes to bottom
                        allCarsWithPositions.Add((car, 999 + car.CarIdx, false)); // Use carIdx to maintain consistent ordering
                    }
                }
            }
            else
            {
                // No fastest lap data at all - all cars get bottom positions
                foreach (var car in allCars)
                {
                    allCarsWithPositions.Add((car, 999 + car.CarIdx, false));
                }
            }

            // Sort by position (cars with times first, then cars without times)
            var sortedByFastestLap = allCarsWithPositions
                .OrderBy(x => x.position)
                .Select(x => x.car)
                .ToList();

            // Find player position in fastest lap order
            int playerFastestLapIndex = sortedByFastestLap.FindIndex(c => c.IsPlayer);
            if (playerFastestLapIndex == -1)
            {
                System.Diagnostics.Debug.WriteLine("[RelativeVM] Player not found in sorted list");
                return new List<RelativeRowViewModel> { playerRow }; // Return just player if something went wrong
            }

            // Build display with player at center, showing cars ahead/behind in fastest lap order
            var result = new List<RelativeRowViewModel>();

            // Add 3 cars ahead of player in fastest lap order
            for (int i = Math.Max(0, playerFastestLapIndex - 3); i < playerFastestLapIndex; i++)
            {
                result.Add(sortedByFastestLap[i]);
            }

            // Add player
            result.Add(playerRow);

            // Add 3 cars behind player in fastest lap order
            for (int i = playerFastestLapIndex + 1; i < Math.Min(sortedByFastestLap.Count, playerFastestLapIndex + 4); i++)
            {
                result.Add(sortedByFastestLap[i]);
            }

            System.Diagnostics.Debug.WriteLine($"[RelativeVM] Built fastest lap display: Player at position {playerFastestLapIndex + 1}/{sortedByFastestLap.Count}");
            return result;
        }

        /// <summary>
        /// Existing proximity-based display for races
        /// </summary>
        private List<RelativeRowViewModel> BuildProximityBasedRows(List<RelativeRowViewModel> allCars,
            int playerCarIdx, float playerLastLapTime)
        {
            var playerRow = allCars.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return new List<RelativeRowViewModel>();

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

        private void CalculatePlayerClassPosition(List<RelativeRowViewModel> allCars, int playerClassID, int playerCarIdx, bool isFastestLapMode, SVappsLABSDKWrapper sdkWrapper)
        {
            if (isFastestLapMode && sdkWrapper != null)
            {
                // Use fastest lap positioning for class position in practice/qualifying
                var fastestLapData = sdkWrapper.GetFastestLapPositioning();

                // Get all cars in player's class
                var classCarIndices = allCars.Where(car => car.ClassID == playerClassID).Select(car => car.CarIdx).ToList();

                // Build class standings including cars without valid times
                var classStandings = new List<(int carIdx, int position, bool hasValidTime)>();

                if (fastestLapData.Any())
                {
                    var fastestLapLookup = fastestLapData.ToDictionary(x => x.carIdx, x => new { x.position, x.fastestTime });

                    foreach (var carIdx in classCarIndices)
                    {
                        if (fastestLapLookup.ContainsKey(carIdx) && fastestLapLookup[carIdx].fastestTime > 0)
                        {
                            // Has valid fastest lap time
                            classStandings.Add((carIdx, fastestLapLookup[carIdx].position, true));
                        }
                        else
                        {
                            // No valid fastest lap time - goes to bottom
                            classStandings.Add((carIdx, 999 + carIdx, false));
                        }
                    }
                }
                else
                {
                    // No fastest lap data - all cars get bottom positions
                    foreach (var carIdx in classCarIndices)
                    {
                        classStandings.Add((carIdx, 999 + carIdx, false));
                    }
                }

                // Sort and find player position
                var sortedClassStandings = classStandings.OrderBy(x => x.position).ToList();
                var playerPosition = sortedClassStandings.FindIndex(x => x.carIdx == playerCarIdx) + 1;

                if (playerPosition > 0)
                {
                    LivePlayerClassPosition = $"P{playerPosition}";
                    LivePlayerClassPositionNumber = playerPosition.ToString();

                    // Only debug when position changes
                    if (playerPosition != _lastPlayerPosition)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RelativeVM] Player fastest lap class position: {playerPosition}/{sortedClassStandings.Count}");
                        _lastPlayerPosition = playerPosition;
                    }
                    return;
                }

                // This shouldn't happen, but fallback just in case
                System.Diagnostics.Debug.WriteLine("[RelativeVM] Player not found in class standings");
            }

            // Default race position sorting for races or fallback
            var racePositionSorted = allCars.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList();
            var carsInClass = racePositionSorted.Where(c => c.ClassID == playerClassID).ToList();

            if (carsInClass.Any())
            {
                int playerClassPosition = carsInClass.FindIndex(c => c.IsPlayer) + 1;
                if (playerClassPosition > 0)
                {
                    LivePlayerClassPosition = $"P{playerClassPosition}";
                    LivePlayerClassPositionNumber = playerClassPosition.ToString();

                    // Only debug when position changes
                    if (playerClassPosition != _lastPlayerPosition)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RelativeVM] Player race class position: {playerClassPosition}/{carsInClass.Count}");
                        _lastPlayerPosition = playerClassPosition;
                    }
                }
            }
        }

        private void ApplyDisplayLogic(List<RelativeRowViewModel> displayRows, List<RelativeRowViewModel> allCars,
            float playerLastLapTime, bool isFastestLapMode, SVappsLABSDKWrapper sdkWrapper)
        {
            var playerRow = displayRows.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return;

            int playerDisplayIndex = displayRows.IndexOf(playerRow);

            // Ensure player's class always gets the first color (Gold)
            int playerClassID = playerRow.ClassID;
            if (playerClassID != 0 && !_classColorMap.ContainsKey(playerClassID))
            {
                _classColorMap[playerClassID] = _classColors[0];
                _nextColorIndex = 1;
            }

            // Build class position maps using RACE POSITION sorting (for class position display)
            var racePositionSorted = allCars.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList();
            var classCars = racePositionSorted.GroupBy(c => c.ClassID).ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList()
            );

            foreach (var row in displayRows)
            {
                // Show class position based on context (fastest lap for practice/qualifying, race position for races)
                if (isFastestLapMode && sdkWrapper != null)
                {
                    // Use fastest lap positioning for individual car positions
                    var fastestLapData = sdkWrapper.GetFastestLapPositioning();
                    if (fastestLapData.Any())
                    {
                        var classCarData = fastestLapData
                            .Where(lap => allCars.Any(car => car.CarIdx == lap.carIdx && car.ClassID == row.ClassID))
                            .OrderBy(lap => lap.position)
                            .ToList();

                        var carPosition = classCarData.FindIndex(lap => lap.carIdx == row.CarIdx) + 1;
                        if (carPosition > 0)
                        {
                            row.ClassPos = $"P{carPosition}";
                        }
                        else
                        {
                            row.ClassPos = "P?";
                        }
                    }
                    else
                    {
                        row.ClassPos = "P?";
                    }
                }
                else
                {
                    // Use race position for individual car positions
                    if (classCars.TryGetValue(row.ClassID, out var carsInThisClass))
                    {
                        int classPosition = carsInThisClass.IndexOf(row) + 1;
                        row.ClassPos = $"P{classPosition}";
                    }
                    else
                    {
                        row.ClassPos = "P?";
                    }
                }

                // Gap calculation depends on mode
                if (isFastestLapMode)
                {
                    CalculateFastestLapGaps(row, playerRow, displayRows, playerDisplayIndex);
                }
                else
                {
                    CalculateProximityGaps(row, playerRow, displayRows, playerDisplayIndex, playerLastLapTime);
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

        private void CalculateFastestLapGaps(RelativeRowViewModel row, RelativeRowViewModel playerRow,
            List<RelativeRowViewModel> displayRows, int playerDisplayIndex)
        {
            if (row.IsPlayer)
            {
                row.Gap = "0.0";
                return;
            }

            int rowDisplayIndex = displayRows.IndexOf(row);

            // For fastest lap mode, gaps represent position difference, not time
            int positionDiff = Math.Abs(rowDisplayIndex - playerDisplayIndex);

            if (rowDisplayIndex < playerDisplayIndex)
            {
                // Car is ahead of player in fastest lap order
                row.Gap = $"+{positionDiff}";
            }
            else
            {
                // Car is behind player in fastest lap order  
                row.Gap = $"-{positionDiff}";
            }
        }

        private void CalculateProximityGaps(RelativeRowViewModel row, RelativeRowViewModel playerRow,
            List<RelativeRowViewModel> displayRows, int playerDisplayIndex, float playerLastLapTime)
        {
            if (row.IsPlayer || playerLastLapTime <= 0)
            {
                row.Gap = "0.0";
                return;
            }

            float playerTrackPercent = playerRow.LapDistPct;
            float carTrackPercent = row.LapDistPct;

            // Calculate track proximity distance (same logic as proximity sorting)
            float directDistance = Math.Abs(carTrackPercent - playerTrackPercent);
            float wrappedDistance = 1.0f - directDistance;
            float proximityDistance = Math.Min(directDistance, wrappedDistance);

            // Detect track wrapping transition
            float rawDifference = carTrackPercent - playerTrackPercent;
            bool isWrappingTransition = Math.Abs(rawDifference) > 0.5f;

            // Convert proximity to time gap
            float rawTimeGap = proximityDistance * playerLastLapTime;

            // Apply smoothing using cache if available
            if (_carCache.ContainsKey(row.CarIdx))
            {
                var cachedRow = _carCache[row.CarIdx];

                // Reset smoothing if wrapping occurred to prevent oscillation
                if (isWrappingTransition)
                {
                    cachedRow.ResetSmoothing();
                }

                cachedRow.UpdateSmoothedGap(rawTimeGap);
                rawTimeGap = cachedRow.SmoothedGap;
            }

            int rowDisplayIndex = displayRows.IndexOf(row);

            if (rowDisplayIndex < playerDisplayIndex)
            {
                // Car is ahead of player in display (rows 1-3) = positive gap
                row.Gap = $"+{rawTimeGap:F1}";
            }
            else if (rowDisplayIndex > playerDisplayIndex)
            {
                // Car is behind player in display (rows 5-7) = negative gap
                row.Gap = $"-{rawTimeGap:F1}";
            }
        }

        private void UpdateCollection(List<RelativeRowViewModel> newRows)
        {
            // Clear and rebuild instead of trying to update in-place
            // This prevents stale data and duplication issues
            RelativeRows.Clear();

            foreach (var row in newRows)
            {
                RelativeRows.Add(row);
            }
        }

        public void Reset()
        {
            RelativeRows.Clear();
            _classColorMap.Clear();
            _nextColorIndex = 0;
            LivePlayerClassPosition = "P--";
            LivePlayerClassPositionNumber = "--";

            // Clear the car cache and reset all smoothing
            foreach (var row in _carCache.Values)
            {
                row.ResetSmoothing();
            }
            _carCache.Clear();
        }
    }
}