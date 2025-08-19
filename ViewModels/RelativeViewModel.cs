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

        private readonly Dictionary<string, Brush> _classColorMap = new();
        private readonly Brush[] _classColors = { Brushes.White, Brushes.Gold, Brushes.DodgerBlue, Brushes.HotPink };
        private int _nextColorIndex = 0;

        public void Update(SVappsLABSnapshot snapshot, SessionDataParser sessionParser)
        {
            // --- DATA READINESS CHECKS ---
            var playerCarIdx = snapshot.GetValue<int>("PlayerCarIdx");
            if (playerCarIdx == -1) return; // Can't do anything if we don't know who the player is.

            // THE FIX: Don't run logic until the session YAML has been parsed.
            if (!sessionParser.IsDataReady) return;

            var carClasses = sessionParser.CarClasses;
            string playerClass = carClasses[playerCarIdx];
            if (string.IsNullOrEmpty(playerClass)) return; // Wait until the player's class is known.

            // --- Get all necessary data arrays ---
            var lapDistPct = snapshot.GetValue<float[]>("CarIdxLapDistPct");
            var currentLap = snapshot.GetValue<int[]>("CarIdxLap");
            var trackSurface = snapshot.GetValue<int[]>("CarIdxTrackSurface");
            var driverNames = sessionParser.DriverNames;
            var carNumbers = sessionParser.CarNumbers;
            var playerLastLapTime = snapshot.GetValue<float>("LapLastLapTime");

            if (lapDistPct == null || currentLap == null || trackSurface == null) return;

            var allValidCars = new List<RelativeRowViewModel>();
            for (int i = 0; i < trackSurface.Length; i++)
            {
                if (trackSurface[i] != (int)iRacingTrackSurface.NotInWorld && carNumbers[i] > 0 && !string.IsNullOrEmpty(driverNames[i]))
                {
                    allValidCars.Add(new RelativeRowViewModel
                    {
                        CarIdx = i,
                        IsPlayer = (i == playerCarIdx),
                        CurrentLap = currentLap[i],
                        LapDistPct = lapDistPct[i],
                        Name = driverNames[i],
                        CarNum = carNumbers[i].ToString(),
                        Class = carClasses[i]
                    });
                }
            }

            var carsInClass = allValidCars.Where(c => c.Class == playerClass).ToList();
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
                float distanceDelta = (row.CurrentLap + row.LapDistPct) - (playerRow.CurrentLap + playerRow.LapDistPct);
                if (playerLastLapTime > 0)
                {
                    row.Gap = (distanceDelta * playerLastLapTime).ToString("F1");
                }
                row.NameColor = Brushes.White;
                if (row.CurrentLap > playerRow.CurrentLap) row.NameColor = Brushes.Red;
                if (row.CurrentLap < playerRow.CurrentLap) row.NameColor = Brushes.CornflowerBlue;
                if (row.IsPlayer) row.NameColor = Brushes.Yellow;
                if (!string.IsNullOrEmpty(row.Class))
                {
                    if (!_classColorMap.ContainsKey(row.Class))
                    {
                        _classColorMap[row.Class] = _classColors[_nextColorIndex % _classColors.Length];
                        _nextColorIndex++;
                    }
                    row.ClassBackground = _classColorMap[row.Class];
                }
            }

            int startIndex = Math.Max(0, playerIndex - 3);
            int count = Math.Min(sortedClass.Count - startIndex, 7);
            return sortedClass.GetRange(startIndex, count);
        }

        private void UpdateCollection(List<RelativeRowViewModel> newRows)
        {
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
                }
                else
                {
                    RelativeRows.Add(newRows[i]);
                }
            }

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
