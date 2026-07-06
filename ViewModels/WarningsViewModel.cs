using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VISOR.Diagnostics;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Drives the incident-counter display (Nx / limit) with severity colouring.
    ///
    /// This class previously also inferred vehicle damage and pit strategy from contact, sub-lap
    /// pace and top speed. That was removed: iRacing exposes no live "damage that matters" signal
    /// (repair time only reads once you're on pit road), so the inference produced nuisance warnings
    /// on inconsequential contact. The incident count is direct, reliable telemetry — it stays.
    /// </summary>
    public class WarningsViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private int _incidentCount = 0;
        private string _incidentDisplay = "0x";
        private Brush _incidentColor = Brushes.White;

        public string IncidentDisplay { get => _incidentDisplay; private set { _incidentDisplay = value; OnPropertyChanged(); } }
        public Brush IncidentColor { get => _incidentColor; private set { _incidentColor = value; OnPropertyChanged(); } }

        public void UpdateIncidentCount(int newCount, int incidentLimit)
        {
            if (newCount != _incidentCount)
            {
                _incidentCount = newCount;
                IncidentDisplay = $"{newCount}x";
                var severity = GetIncidentSeverity(newCount, incidentLimit);
                IncidentColor = SeverityToBrush(severity);
                Log.Info($"[Warnings] Incident count: {newCount}x / {(incidentLimit > 0 ? incidentLimit.ToString() : "unlimited")} ({severity})");
            }
        }

        private enum IncidentSeverity { Clear, Caution, Danger }

        // Colour by how close the count is to the session's incident limit, not by an absolute count —
        // so the meaning ("getting dangerous") holds whether the cap is 4x, 17x or 25x. Yellow at 30%
        // of the limit, red at 60%. Unlimited sessions (limit 0) have no DQ stakes, so fall back to a
        // gentle absolute scale that's purely informational.
        private const float INCIDENT_CAUTION_FRACTION = 0.30f;
        private const float INCIDENT_DANGER_FRACTION = 0.60f;

        private static IncidentSeverity GetIncidentSeverity(int count, int limit)
        {
            if (count <= 0) return IncidentSeverity.Clear;

            if (limit > 0)
            {
                float fraction = (float)count / limit;
                if (fraction >= INCIDENT_DANGER_FRACTION) return IncidentSeverity.Danger;
                if (fraction >= INCIDENT_CAUTION_FRACTION) return IncidentSeverity.Caution;
                return IncidentSeverity.Clear;
            }

            // Unlimited: no cap to measure against.
            if (count > 12) return IncidentSeverity.Danger;
            if (count > 4) return IncidentSeverity.Caution;
            return IncidentSeverity.Clear;
        }

        private static Brush SeverityToBrush(IncidentSeverity severity) => severity switch
        {
            IncidentSeverity.Danger => Brushes.Red,
            IncidentSeverity.Caution => Brushes.Yellow,
            _ => Brushes.White
        };

        public void Reset()
        {
            _incidentCount = 0;
            IncidentDisplay = "0x";
            IncidentColor = Brushes.White;
        }
    }
}
