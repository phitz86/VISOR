using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.ViewModels
{
    /// <summary>
    /// Drives the Row 5 pace graph: one diverging bar per completed lap showing how the
    /// player's pace against the class is *trending* — green up when a lap beat the player's
    /// own recent norm vs. the field, red down when it fell short (matching the Row 2 delta
    /// bar's colour semantics).
    ///
    /// The metric is deliberately second-order. A raw "you vs. the class median" delta mostly
    /// encodes who is in the split: a 1600-iRating driver in a 4k split reads pinned slow every
    /// lap, and the same driver in an even split reads mid-pack — neither says anything useful
    /// mid-race. So each lap's delta to the class median is re-centred on the median of the
    /// player's own deltas over their recent valid laps. What survives is the strategic signal:
    /// a bar grows only when the player's relationship to the field CHANGES — holding on while
    /// the field falls away, or fading while the field picks up. Comparing per lap number also
    /// self-corrects for anything that slows everyone (track evolution, weather).
    ///
    /// Lap hygiene, so a pit cycle or caution can't masquerade as a pace change:
    ///  - Laps touched by pit road (in-lap and out-lap) are excluded on both sides.
    ///  - Laps run under a full-course caution (Caution/CautionWaving) are excluded. Local
    ///    yellows are not: they affect few cars, and the median absorbs them.
    ///  - Lap 1 (standing start / grid order) is excluded.
    ///  - Excluded player laps still occupy a slot, drawn as a grey tick, so one slot always
    ///    equals one lap and pit cycles stay visible in the history.
    /// The player's own valid-but-slow laps (spins, contact) are shown truthfully, clamped.
    /// </summary>
    public class PaceGraphViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private const int MaxCars = 64;
        private const int DisplayLaps = 14;
        private const int MinReferencePool = 3;      // class laps needed before a delta exists
        private const int BaselineWindowLaps = 10;   // player's norm = median of this many recent deltas
        private const int MinBaselineLaps = 3;       // deltas needed before a norm (and a bar) exists
        private const int PlayerLapRetention = DisplayLaps + BaselineWindowLaps + 6;
        private const int PoolRetentionLaps = DisplayLaps + BaselineWindowLaps + 8;

        // Deviations from the player's own norm are small — tenths, not seconds — so the
        // clamp is much tighter than a raw-delta scale would be.
        private const float MaxDeviationSeconds = 1.5f;
        private const double HalfHeightPx = 25.0;    // bar area above/below the baseline
        private const double MinBarHeightPx = 2.0;   // a dead-even lap still shows a stub

        private const int FullCourseCautionMask =
            (int)(SessionFlags.Caution | SessionFlags.CautionWaving);

        // --- Per-car lap tracking ---
        private readonly int[] _lastLapCompleted = new int[MaxCars];
        private readonly bool[] _pitTainted = new bool[MaxCars];
        private readonly bool[] _cautionTainted = new bool[MaxCars];
        private bool _seeded;

        // Reference laps from other cars: lap number -> (classId, lapTime) entries.
        private readonly Dictionary<int, List<(int classId, float time)>> _referencePool = new();

        // The player's completed laps, oldest first. Excluded laps keep their slot.
        private readonly List<(int lap, float time, bool excluded)> _playerLaps = new();

        private bool _graphVisible;
        private IReadOnlyList<PaceSlotViewModel> _slots = Array.Empty<PaceSlotViewModel>();

        public bool GraphVisible { get => _graphVisible; private set { _graphVisible = value; OnPropertyChanged(); } }
        public IReadOnlyList<PaceSlotViewModel> Slots { get => _slots; private set { _slots = value; OnPropertyChanged(); } }

        public void Update(SVappsLABSnapshot snapshot, ISessionDataProvider dataProvider)
        {
            int playerIdx = snapshot.PlayerCarIdx;
            if (playerIdx < 0)
                return;

            var lapCompleted = snapshot.CarIdxLapCompleted;
            var lastLapTimes = snapshot.CarIdxLastLapTime;
            var onPitRoad = snapshot.CarIdxOnPitRoad;
            var classIds = dataProvider.CarClassIDs;
            bool cautionNow = (snapshot.SessionFlags & FullCourseCautionMask) != 0;

            // First frame only seeds the completed-lap counters: on a mid-session join the
            // arrays already hold history whose lap times we never observed being set.
            if (!_seeded)
            {
                for (int i = 0; i < MaxCars; i++)
                    _lastLapCompleted[i] = lapCompleted[i];
                _seeded = true;
                return;
            }

            bool poolChanged = false;

            for (int i = 0; i < MaxCars; i++)
            {
                if (onPitRoad[i]) _pitTainted[i] = true;
                if (cautionNow) _cautionTainted[i] = true;

                int completed = lapCompleted[i];
                if (completed <= _lastLapCompleted[i])
                {
                    // A backwards jump means a session reset for this car; resync silently.
                    if (completed < _lastLapCompleted[i])
                        _lastLapCompleted[i] = completed;
                    continue;
                }

                float lapTime = lastLapTimes[i];
                bool tainted = _pitTainted[i] || _cautionTainted[i] || completed <= 1;
                bool hasTime = lapTime > 0f;

                if (i == playerIdx)
                {
                    _playerLaps.Add((completed, lapTime, excluded: tainted || !hasTime));
                    if (_playerLaps.Count > PlayerLapRetention)
                        _playerLaps.RemoveAt(0);
                    Log.Debug($"[PaceGraph] Player lap {completed}: {lapTime:F3}s" +
                              (tainted || !hasTime ? " (excluded)" : ""));
                    poolChanged = true;
                }
                else if (hasTime && !tainted && i < classIds.Length)
                {
                    if (!_referencePool.TryGetValue(completed, out var entries))
                    {
                        entries = new List<(int, float)>();
                        _referencePool[completed] = entries;
                    }
                    entries.Add((classIds[i], lapTime));
                    poolChanged = true;
                }

                _lastLapCompleted[i] = completed;
                // The new lap inherits taint from its starting conditions.
                _pitTainted[i] = onPitRoad[i];
                _cautionTainted[i] = cautionNow;
            }

            if (poolChanged)
            {
                PruneReferencePool();
                RebuildSlots(playerIdx, classIds);
            }
        }

        private void PruneReferencePool()
        {
            if (_playerLaps.Count == 0) return;
            int cutoff = _playerLaps[^1].lap - PoolRetentionLaps;
            if (cutoff <= 0) return;

            List<int>? stale = null;
            foreach (int lap in _referencePool.Keys)
            {
                if (lap < cutoff) (stale ??= new List<int>()).Add(lap);
            }
            if (stale != null)
                foreach (int lap in stale) _referencePool.Remove(lap);
        }

        private void RebuildSlots(int playerIdx, int[] classIds)
        {
            int playerClassId = playerIdx < classIds.Length ? classIds[playerIdx] : 0;

            // Pass 1: raw delta to the class median for every retained player lap (null when
            // the lap is excluded or too few classmates have run that lap number yet — a later
            // completion triggers another rebuild and fills these in).
            var deltas = new float?[_playerLaps.Count];
            for (int i = 0; i < _playerLaps.Count; i++)
            {
                var (lap, time, excluded) = _playerLaps[i];
                if (excluded) continue;
                float? median = GetClassMedian(lap, playerClassId);
                if (median != null)
                    deltas[i] = time - median.Value;
            }

            // Pass 2: each displayed lap is drawn as its deviation from the player's norm —
            // the median of their previous valid deltas — so a constant skill gap to the split
            // cancels out and only a *change* in pace vs. the field grows a bar.
            var slots = new List<PaceSlotViewModel>(DisplayLaps);
            int start = Math.Max(0, _playerLaps.Count - DisplayLaps);
            int barCount = 0;

            for (int i = start; i < _playerLaps.Count; i++)
            {
                float? norm = deltas[i] != null ? GetBaselineNorm(deltas, i) : null;
                if (norm == null)
                {
                    slots.Add(PaceSlotViewModel.Tick());
                    continue;
                }

                float deviation = Math.Clamp(deltas[i]!.Value - norm.Value,
                    -MaxDeviationSeconds, MaxDeviationSeconds);
                double height = Math.Max(MinBarHeightPx,
                    Math.Abs(deviation) / MaxDeviationSeconds * HalfHeightPx);
                slots.Add(PaceSlotViewModel.Bar(fasterThanField: deviation <= 0f, height));
                barCount++;
            }

            bool becameVisible = !GraphVisible && barCount > 0;
            Slots = slots;
            GraphVisible = barCount > 0;
            if (becameVisible)
                Log.Info($"[PaceGraph] Visible with {barCount} trend bars");
        }

        /// <summary>
        /// The player's norm for lap index <paramref name="i"/>: the median of their previous
        /// valid deltas, newest-first, up to the baseline window. Null until enough history
        /// exists. Excluding the lap itself keeps a one-lap swing from muting its own bar.
        /// </summary>
        private static float? GetBaselineNorm(float?[] deltas, int i)
        {
            var window = new List<float>(BaselineWindowLaps);
            for (int j = i - 1; j >= 0 && window.Count < BaselineWindowLaps; j--)
            {
                if (deltas[j] != null)
                    window.Add(deltas[j]!.Value);
            }

            if (window.Count < MinBaselineLaps)
                return null;

            window.Sort();
            int mid = window.Count / 2;
            return window.Count % 2 == 1
                ? window[mid]
                : (window[mid - 1] + window[mid]) / 2f;
        }

        private float? GetClassMedian(int lap, int playerClassId)
        {
            if (!_referencePool.TryGetValue(lap, out var entries))
                return null;

            var classTimes = entries
                .Where(e => e.classId == playerClassId)
                .Select(e => e.time)
                .OrderBy(t => t)
                .ToList();

            if (classTimes.Count < MinReferencePool)
                return null;

            int mid = classTimes.Count / 2;
            return classTimes.Count % 2 == 1
                ? classTimes[mid]
                : (classTimes[mid - 1] + classTimes[mid]) / 2f;
        }

        public void Reset()
        {
            Array.Clear(_lastLapCompleted);
            Array.Clear(_pitTainted);
            Array.Clear(_cautionTainted);
            _seeded = false;
            _referencePool.Clear();
            _playerLaps.Clear();
            GraphVisible = false;
            Slots = Array.Empty<PaceSlotViewModel>();
        }
    }

    /// <summary>
    /// One lap slot in the pace graph. Bars render as a rectangle growing up (faster, green)
    /// or down (slower, red) from the baseline; excluded/pending laps render as a grey tick.
    /// Heights are pre-scale pixels; the window's LayoutTransform handles size presets.
    /// </summary>
    public class PaceSlotViewModel
    {
        private static readonly Brush FasterBrush = Brushes.Green;
        private static readonly Brush SlowerBrush = Brushes.Red;

        public double UpHeight { get; private init; }
        public double DownHeight { get; private init; }
        public Brush BarBrush { get; private init; } = Brushes.Transparent;
        public bool IsTick { get; private init; }

        public static PaceSlotViewModel Tick() => new() { IsTick = true, BarBrush = Brushes.Gray };

        public static PaceSlotViewModel Bar(bool fasterThanField, double height) => new()
        {
            UpHeight = fasterThanField ? height : 0.0,
            DownHeight = fasterThanField ? 0.0 : height,
            BarBrush = fasterThanField ? FasterBrush : SlowerBrush
        };
    }
}
