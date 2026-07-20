using System.ComponentModel;
using System.Runtime.CompilerServices;
using VISOR.Diagnostics;
using VISOR.Telemetry;
using VISOR.TrackData;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Drives the Row 5 track-location readout: the name of the corner or section of the
    /// circuit the player is currently in ("Eau Rouge", "Kemmel Straight"), resolved from
    /// the static Data/TrackSections.json catalog by the player's LapDistPct. There is no
    /// sign hanging over the real track surface — this is that sign.
    ///
    /// Section boundaries in the catalog are hand-estimated, so the display never has to be
    /// metre-accurate — but it must never flap between two names at a boundary. A candidate
    /// section therefore only replaces the displayed one after it has held for a short dwell
    /// time; noise that straddles a boundary keeps resetting the dwell clock and the shown
    /// name stays put. At racing speed the dwell is imperceptible.
    ///
    /// On pit road the catalog's percentages describe the racing line the car is not on, so
    /// the readout says "Pit Lane" instead. Off-world (garage, towing: LapDistPct &lt; 0)
    /// the element hides entirely, as it does on any track the catalog doesn't know.
    /// </summary>
    public class TrackLocationViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private const double ConfirmDwellSec = 0.25;
        private const string PitLaneLabel = "Pit Lane";

        private TrackSectionSet? _sectionSet;
        private string? _resolvedTrackKey;

        private int _displayedIdx = -1;
        private int _pendingIdx = -1;
        private double _pendingSince;

        private string _sectionName = string.Empty;
        private bool _locationVisible;

        public string SectionName { get => _sectionName; private set { _sectionName = value; OnPropertyChanged(); } }
        public bool LocationVisible { get => _locationVisible; private set { _locationVisible = value; OnPropertyChanged(); } }

        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            int playerIdx = snapshot.PlayerCarIdx;
            if (playerIdx < 0)
            {
                Hide();
                return;
            }

            ResolveTrackIfChanged(dataProvider);
            if (_sectionSet == null)
            {
                Hide();
                return;
            }

            if (snapshot.CarIdxOnPitRoad[playerIdx])
            {
                // Force a fresh section fix on pit exit rather than resuming a stale one.
                _displayedIdx = -1;
                _pendingIdx = -1;
                Show(PitLaneLabel);
                return;
            }

            float pct = snapshot.CarIdxLapDistPct[playerIdx];
            if (pct < 0f || pct > 1f)
            {
                Hide();
                return;
            }

            double now = snapshot.SessionTime;
            if (now < _pendingSince)
                _pendingIdx = -1;

            int idx = SectionIndexAt(pct);
            if (idx == _displayedIdx)
            {
                _pendingIdx = -1;
            }
            else if (_displayedIdx < 0 || (idx == _pendingIdx && now - _pendingSince >= ConfirmDwellSec))
            {
                _displayedIdx = idx;
                _pendingIdx = -1;
                Log.Debug($"[TrackLocation] {_sectionSet.Sections[idx].Name} (pct {pct:F4})");
            }
            else if (idx != _pendingIdx)
            {
                _pendingIdx = idx;
                _pendingSince = now;
            }

            if (_displayedIdx >= 0)
                Show(_sectionSet.Sections[_displayedIdx].Name);
        }

        /// <summary>
        /// The section whose range contains <paramref name="pct"/>: the last section starting
        /// at or before it. A pct ahead of the first boundary belongs to the lap's final
        /// section wrapping across the start/finish line.
        /// </summary>
        private int SectionIndexAt(float pct)
        {
            var sections = _sectionSet!.Sections;
            for (int i = sections.Count - 1; i >= 0; i--)
            {
                if (pct >= sections[i].Pct)
                    return i;
            }
            return sections.Count - 1;
        }

        private void ResolveTrackIfChanged(ISessionDataProvider dataProvider)
        {
            // Track identity is coordinator-derived, not telemetry (same pattern as the radar's
            // track length lookup).
            if (dataProvider is not SessionDataCoordinator coordinator)
                return;

            string trackName = coordinator.GetTrackName();
            string trackConfig = coordinator.GetTrackConfig();
            string key = trackName + "|" + trackConfig;
            if (key == _resolvedTrackKey)
                return;

            _resolvedTrackKey = key;
            _displayedIdx = -1;
            _pendingIdx = -1;
            _sectionSet = TrackSectionCatalog.Resolve(
                trackName, coordinator.GetTrackDisplayName(), trackConfig);

            Log.Info(_sectionSet != null
                ? $"[TrackLocation] '{trackName}' ({trackConfig}) -> {_sectionSet.Track}, {_sectionSet.Sections.Count} sections"
                : $"[TrackLocation] No section data for '{trackName}' ({trackConfig})");
        }

        private void Show(string name)
        {
            if (_sectionName != name) SectionName = name;
            if (!_locationVisible) LocationVisible = true;
        }

        private void Hide()
        {
            if (_locationVisible) LocationVisible = false;
            _displayedIdx = -1;
            _pendingIdx = -1;
        }

        public void Reset()
        {
            _resolvedTrackKey = null;
            _sectionSet = null;
            SectionName = string.Empty;
            Hide();
        }
    }
}
