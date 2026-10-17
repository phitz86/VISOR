using System.Collections.ObjectModel;
using VISOR.Race;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    public class RelativeViewModel
    {
        public ObservableCollection<RelativeRowViewModel> RelativeRows { get; } = new();

        private readonly RelativeDisplayBuilder _builder;

        public RelativeViewModel(ClassColorManager classColorManager, PositionCalculator positionCalculator)
        {
            _builder = new RelativeDisplayBuilder(classColorManager, positionCalculator);
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
        }
    }
}