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

        // --- PUBLIC ACCESSOR FOR DEBUG LOGGERS ---
        public PositionCalculator PositionCalculator { get; }

        public RelativeViewModel(ClassColorManager classColorManager, PositionCalculator positionCalculator)
        {
            // Store PositionCalculator for external access
            PositionCalculator = positionCalculator;

            // Pass dependencies to the Builder
            _builder = new RelativeDisplayBuilder(_carCache, classColorManager, positionCalculator);
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

            foreach (var row in _carCache.Values)
            {
                row.ResetSmoothing();
            }
            _carCache.Clear();
        }
    }
}