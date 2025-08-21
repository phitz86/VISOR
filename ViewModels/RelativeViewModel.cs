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
            if (!sessionDataProvider.IsDataReady) return;

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
                        ClassID = carClassIDs[i], // Updated to use ClassID instead of Class
                        IncidentCount = incidentCounts[i]
                    });
                }
            }

            // Filter to cars in the same class as the player (using integer comparison)
            var carsInClass = allValidCars.Where(c => c.ClassID == playerClassID).ToList();
            if (!carsInClass.Any()) return;

            var sortedClass = carsInClass.OrderByDescending(c => c.CurrentLap + c.LapDistPct).ToList();
            int playerLivePosition = sortedClass.FindIndex(c => c.IsPlayer) + 1;

            if (playerLivePosition > 0)
            {
                LivePlayerClassPosition = $"P{playerLivePosition}";
                LivePlayerClassPositionNumber = playerLivePosition.ToString();
            }

            var finalRows = BuildRelativeRows(sortedClass, playerCarIdx, playerLastLapTime);
            UpdateCollection(finalRows);
        }

        private List<RelativeRowViewModel> BuildRelativeRows(List<RelativeRowViewModel> sortedClass, int playerCarIdx, float playerLastLapTime)
        {
            var playerRow = sortedClass.FirstOrDefault(r => r.IsPlayer);
            if (playerRow == null) return new List<RelativeRowViewModel>();

            int playerIndex = sortedClass.IndexOf(playerRow);

            foreach (var row in sortedClass)
            {
                row.ClassPos = $"P{sortedClass.IndexOf(row) + 1}";

                // Calculate gap based on distance delta
                float distanceDelta = (row.CurrentLap + row.LapDistPct) - (playerRow.CurrentLap + playerRow.LapDistPct);
                if (playerLastLapTime > 0)
                {
                    row.Gap = (distanceDelta * playerLastLapTime).ToString("F1");
                }

                // Set name color based on lap differential
                row.NameColor = Brushes.White;
                if (row.CurrentLap > playerRow.CurrentLap) row.NameColor = Brushes.Red;
                if (row.CurrentLap < playerRow.CurrentLap) row.NameColor = Brushes.CornflowerBlue;
                if (row.IsPlayer) row.NameColor = Brushes.Yellow;

                // Assign class background color based on class ID
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

            // Return a 7-car window centered on the player (3 ahead, player, 3 behind)
            int startIndex = Math.Max(0, playerIndex - 3);
            int count = Math.Min(sortedClass.Count - startIndex, 7);
            return sortedClass.GetRange(startIndex, count);
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