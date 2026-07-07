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
    /// Drives the Row 5 pace graph: one diverging bar per completed lap showing the player's
    /// lap time against the class median for that same lap number — green up when faster than
    /// the field, red down when slower (matching the Row 2 delta bar's colour semantics).
    ///
    /// This displays measured data, never an inference: each bar is "your lap N vs. what your
    /// class actually ran on lap N". Comparing per lap number self-corrects for anything that
    /// slows the whole field (track evolution, weather), so the graph isolates the strategic
    /// signal — are *you* gaining or losing on the cars you race against.
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
        private const int MinValidPlayerLaps = 3;    // graph stays hidden until this many
        private const int MinReferencePool = 3;      // class laps needed before a bar can draw
        private const int PoolRetentionLaps = 25;    // prune reference data older than this

        private const float MaxDeltaSeconds = 3.0f;
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
                    if (_playerLaps.Count > DisplayLaps + 2)
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
            int validCount = _playerLaps.Count(l => !l.excluded);
            if (validCount < MinValidPlayerLaps)
            {
                GraphVisible = false;
                Slots = Array.Empty<PaceSlotViewModel>();
                return;
            }

            int playerClassId = playerIdx < classIds.Length ? classIds[playerIdx] : 0;
            var slots = new List<PaceSlotViewModel>(DisplayLaps);

            foreach (var (lap, time, excluded) in _playerLaps.Skip(Math.Max(0, _playerLaps.Count - DisplayLaps)))
            {
                if (excluded)
                {
                    slots.Add(PaceSlotViewModel.Tick());
                    continue;
                }

                float? median = GetClassMedian(lap, playerClassId);
                if (median == null)
                {
                    // Not enough classmates have run this lap (yet) — a later completion
                    // triggers another rebuild and the bar fills in.
                    slots.Add(PaceSlotViewModel.Tick());
                    continue;
                }

                float delta = Math.Clamp(time - median.Value, -MaxDeltaSeconds, MaxDeltaSeconds);
                double height = Math.Max(MinBarHeightPx, Math.Abs(delta) / MaxDeltaSeconds * HalfHeightPx);
                slots.Add(PaceSlotViewModel.Bar(fasterThanField: delta <= 0f, height));
            }

            bool becameVisible = !GraphVisible;
            Slots = slots;
            GraphVisible = true;
            if (becameVisible)
                Log.Info($"[PaceGraph] Visible with {validCount} valid laps");
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
