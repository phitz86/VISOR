using System.Collections.ObjectModel;
using VISOR.Race;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class RelativeViewModel
    {
        public ObservableCollection<RelativeRowViewModel> RelativeRows { get; } = new();
        private readonly Dictionary<int, RelativeRowViewModel> _carCache = new();

        private readonly RelativeDisplayBuilder _builder;
        private readonly Telemetry.PositionHistoryManager _historyManager;

        public RelativeViewModel(ClassColorManager classColorManager, PositionCalculator positionCalculator)
        {
            _historyManager = new Telemetry.PositionHistoryManager();
            _builder = new RelativeDisplayBuilder(_carCache, classColorManager, positionCalculator, _historyManager);
        }

        public void Update(SVappsLABSnapshot snapshot, SessionView? sessionDataProvider)
        {
            if (snapshot.PlayerCarIdx == -1 ||
                sessionDataProvider == null ||
                !sessionDataProvider.IsDataReady)
            {
                return;
            }

            // Alone on track in lone qualifying: the relative hides.
            if (sessionDataProvider.IsLoneQualifying)
            {
                if (RelativeRows.Count > 0) RelativeRows.Clear();
                return;
            }

            var newRows = _builder.Calculate(snapshot, sessionDataProvider);
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
            _historyManager.Reset();

            foreach (var row in _carCache.Values)
            {
                row.ResetSmoothing();
            }
            _carCache.Clear();
        }
    }
}