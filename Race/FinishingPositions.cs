namespace VISOR.Race
{
    /// <summary>
    /// The places frozen at the finish. <see cref="FinishTracker"/> writes them as cars take the
    /// checkered flag, or leave under it; <see cref="RunningOrder"/> reads them so live cars skip
    /// the frozen slots, and <see cref="PositionCalculator"/> reports a frozen place ahead of the
    /// live one. Cleared on reset and on a session change.
    /// </summary>
    internal sealed class FinishingPositions
    {
        private readonly Dictionary<int, int> _finishingClassPositions = new();
        private readonly Dictionary<int, int> _finishingOverallPositions = new();

        /// <summary>Frozen class position by car index.</summary>
        public IReadOnlyDictionary<int, int> ClassPositions => _finishingClassPositions;

        /// <summary>Frozen overall position by car index. Same cars as <see cref="ClassPositions"/>.</summary>
        public IReadOnlyDictionary<int, int> OverallPositions => _finishingOverallPositions;

        /// <summary>Frozen: its place no longer changes, and it is left out of the live sort.</summary>
        public bool IsFinished(int carIdx) => _finishingClassPositions.ContainsKey(carIdx);

        public void Freeze(int carIdx, int classPosition, int overallPosition)
        {
            _finishingClassPositions[carIdx] = classPosition;
            _finishingOverallPositions[carIdx] = overallPosition;
        }

        public void Clear()
        {
            _finishingClassPositions.Clear();
            _finishingOverallPositions.Clear();
        }
    }
}
