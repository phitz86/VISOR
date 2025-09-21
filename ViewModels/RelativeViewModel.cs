using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    // MODIFIED: Moved the enum outside the class to be accessible by other classes in the namespace.
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

        // --- State Properties ---
        public ObservableCollection<RelativeRowViewModel> RelativeRows { get; } = new();
        private readonly Dictionary<int, RelativeRowViewModel> _carCache = new();

        // --- Child Services ---
        private readonly RelativeDisplayCalculator _calculator;

        // --- Live Player Position ---
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

        public RelativeViewModel(ClassColorManager classColorManager)
        {
            _calculator = new RelativeDisplayCalculator(_carCache, classColorManager);
        }

        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            // --- Data Readiness Checks ---
            if (snapshot.GetValue<int>("PlayerCarIdx", -1) == -1 || sessionDataProvider == null || !sessionDataProvider.IsDataReady)
            {
                return;
            }

            // Hide for lone qualifying
            if (sessionDataProvider.ShouldHideRelativeDisplay())
            {
                if (RelativeRows.Count > 0) RelativeRows.Clear();
                LivePlayerClassPosition = "P--";
                LivePlayerClassPositionNumber = "--";
                return;
            }

            // --- Delegate to Calculator ---
            var result = _calculator.Calculate(snapshot, sessionDataProvider);

            // --- Update State from Result ---
            LivePlayerClassPosition = result.PlayerPos;
            LivePlayerClassPositionNumber = result.PlayerPosNum;
            UpdateCollection(result.Rows);
        }

        private void UpdateCollection(List<RelativeRowViewModel> newRows)
        {
            if (newRows.Count == 0 && RelativeRows.Count == 0) return;

            for (int i = 0; i < newRows.Count; i++)
            {
                if (i < RelativeRows.Count)
                {
                    if (RelativeRows[i].CarIdx != newRows[i].CarIdx)
                    {
                        RelativeRows[i] = newRows[i];
                    }
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
            _calculator.Reset();

            foreach (var row in _carCache.Values)
            {
                row.ResetSmoothing();
            }
            _carCache.Clear();

            LivePlayerClassPosition = "P--";
            LivePlayerClassPositionNumber = "--";
        }
    }
}