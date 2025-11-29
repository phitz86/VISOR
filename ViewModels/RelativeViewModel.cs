using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
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
        private readonly RelativeDisplayBuilder _builder;
        private readonly ClassPaceManager _paceManager; // Added

        public RelativeViewModel(ClassColorManager classColorManager, PositionCalculator positionCalculator)
        {
            // Initialize the Pace Manager
            _paceManager = new ClassPaceManager();

            // Pass it to the Builder
            _builder = new RelativeDisplayBuilder(_carCache, classColorManager, positionCalculator, _paceManager);
        }

        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider sessionDataProvider)
        {
            if (snapshot.GetValue<int>("PlayerCarIdx", -1) == -1 ||
                sessionDataProvider == null ||
                !sessionDataProvider.IsDataReady)
            {
                return;
            }

            if (sessionDataProvider.ShouldHideRelativeDisplay())
            {
                if (RelativeRows.Count > 0) RelativeRows.Clear();
                return;
            }

            // CRITICAL: Update the Pace Manager with the latest session data
            // This ensures scalars evolve as the race progresses
            if (sessionDataProvider is SessionDataCoordinator coordinator)
            {
                _paceManager.Update(coordinator.GetLiveSessionData(), coordinator.GetStaticEventData());
            }

            // --- Delegate to Builder ---
            var newRows = _builder.Calculate(snapshot, sessionDataProvider);

            // --- Update State from Result ---
            UpdateCollection(newRows);
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
            _builder.Reset();
            _paceManager.Reset(); // Reset scalars on session change

            foreach (var row in _carCache.Values)
            {
                row.ResetSmoothing();
            }
            _carCache.Clear();
        }
    }
}