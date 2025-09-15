using System;
using System.Collections.Generic;
using System.Linq;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Coordinates all parsing and provides unified access to session data
    /// </summary>
    public class SessionDataCoordinator : ISessionDataProvider
    {
        private readonly StaticEventParser _staticParser = new();
        private readonly SessionTransitionParser _transitionParser = new();
        private readonly LiveSessionParser _liveParser = new();

        private readonly StaticEventData _staticData = new();
        private readonly SessionTransitionData _transitionData = new();
        private readonly LiveSessionData _liveData = new();

        private string _lastDataHash = string.Empty;
        private readonly object _parseLock = new();

        public bool IsDataReady { get; private set; }

        // Private backing fields for cached arrays to improve performance
        private readonly string[] _userNamesCache = new string[64];
        private readonly string[] _carNumbersCache = new string[64];
        private readonly int[] _carNumberRawCache = new int[64];
        private readonly int[] _carClassIDsCache = new int[64];
        private readonly bool[] _carIsAICache = new bool[64];
        private readonly int[] _curDriverIncidentCountCache = new int[64];

        // ========== ISessionDataProvider Implementation ==========

        public string[] UserNames
        {
            get
            {
                lock (_parseLock) { return _userNamesCache; }
            }
        }

        public string[] CarNumbers
        {
            get
            {
                lock (_parseLock) { return _carNumbersCache; }
            }
        }

        public int[] CarNumberRaw
        {
            get
            {
                lock (_parseLock) { return _carNumberRawCache; }
            }
        }

        public int[] CarClassIDs
        {
            get
            {
                lock (_parseLock) { return _carClassIDsCache; }
            }
        }

        public bool[] CarIsAI
        {
            get
            {
                lock (_parseLock) { return _carIsAICache; }
            }
        }

        public int[] CurDriverIncidentCount
        {
            get
            {
                lock (_parseLock) { return _curDriverIncidentCountCache; }
            }
        }

        public int IncidentLimit
        {
            get { lock (_parseLock) { return _staticData.IncidentLimit; } }
        }

        // ========== Session-Aware Methods ==========

        public int CurrentSessionNum
        {
            get { lock (_parseLock) { return _transitionData.CurrentSessionNum; } }
        }

        public string GetSessionType(int sessionNum)
        {
            lock (_parseLock)
            {
                return _staticData.Schedule.Sessions.TryGetValue(sessionNum, out var session)
                    ? session.SessionType
                    : string.Empty;
            }
        }

        public string GetSessionName(int sessionNum)
        {
            lock (_parseLock)
            {
                return _staticData.Schedule.Sessions.TryGetValue(sessionNum, out var session)
                    ? session.SessionName
                    : string.Empty;
            }
        }

        public string GetCurrentSessionType()
        {
            return GetSessionType(CurrentSessionNum);
        }

        public string GetCurrentSessionName()
        {
            return GetSessionName(CurrentSessionNum);
        }

        public bool IsPracticeSession(int sessionNum)
        {
            var sessionType = GetSessionType(sessionNum);
            return sessionType.Contains("Practice", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsQualifyingSession(int sessionNum)
        {
            var sessionType = GetSessionType(sessionNum);
            return sessionType.Contains("Qualify", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsLoneQualifying(int sessionNum)
        {
            var sessionType = GetSessionType(sessionNum);
            return sessionType.Contains("Lone", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsRaceSession(int sessionNum)
        {
            var sessionType = GetSessionType(sessionNum);
            return sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);
        }

        public Dictionary<int, string> GetSessionSchedule()
        {
            lock (_parseLock)
            {
                var schedule = new Dictionary<int, string>();
                foreach (var kvp in _staticData.Schedule.Sessions)
                    schedule[kvp.Key] = kvp.Value.SessionType;
                return schedule;
            }
        }

        public double GetSessionTimeSeconds(int sessionNum)
        {
            lock (_parseLock)
            {
                return _staticData.Schedule.Sessions.TryGetValue(sessionNum, out var session)
                    ? session.SessionTimeSeconds
                    : 0.0;
            }
        }

        public int GetSessionLaps(int sessionNum)
        {
            lock (_parseLock)
            {
                return _staticData.Schedule.Sessions.TryGetValue(sessionNum, out var session)
                    ? session.SessionLaps
                    : -1;
            }
        }

        // ========== Track Information Access ==========

        /// <summary>
        /// Get track length in meters from YAML data
        /// </summary>
        public float GetTrackLength()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackLength;
            }
        }

        /// <summary>
        /// Get track name from YAML data
        /// </summary>
        public string GetTrackName()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackName;
            }
        }

        /// <summary>
        /// Get track configuration name from YAML data
        /// </summary>
        public string GetTrackConfig()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackConfig;
            }
        }

        /// <summary>
        /// Get track display name from YAML data
        /// </summary>
        public string GetTrackDisplayName()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackDisplayName;
            }
        }

        /// <summary>
        /// Get track display short name from YAML data
        /// </summary>
        public string GetTrackDisplayShortName()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackDisplayShortName;
            }
        }

        // ========== Live Results Access ==========

        public List<LiveSessionData.ResultPosition> GetCurrentSessionResultsPositions()
        {
            lock (_parseLock)
            {
                return _liveData.SessionResultsPositions.GetValueOrDefault(CurrentSessionNum,
                    new List<LiveSessionData.ResultPosition>());
            }
        }

        public List<LiveSessionData.ResultPosition> GetSessionResultsPositions(int sessionNum)
        {
            lock (_parseLock)
            {
                return _liveData.SessionResultsPositions.GetValueOrDefault(sessionNum,
                    new List<LiveSessionData.ResultPosition>());
            }
        }

        public List<LiveSessionData.FastestLapResult> GetCurrentSessionFastestLaps()
        {
            lock (_parseLock)
            {
                return _liveData.SessionFastestLaps.GetValueOrDefault(CurrentSessionNum,
                    new List<LiveSessionData.FastestLapResult>());
            }
        }

        public List<LiveSessionData.FastestLapResult> GetSessionFastestLaps(int sessionNum)
        {
            lock (_parseLock)
            {
                return _liveData.SessionFastestLaps.GetValueOrDefault(sessionNum,
                    new List<LiveSessionData.FastestLapResult>());
            }
        }

        // Legacy qualify results (backward compatibility)
        public int[] GetQualifyResultsPositions()
        {
            lock (_parseLock)
            {
                var positions = new int[64];
                foreach (var kvp in _liveData.QualifyPositions)
                    positions[kvp.Key] = kvp.Value;
                return positions;
            }
        }

        public float[] GetQualifyResultsFastestTimes()
        {
            lock (_parseLock)
            {
                var times = new float[64];
                foreach (var kvp in _liveData.QualifyFastestTimes)
                    times[kvp.Key] = kvp.Value;
                return times;
            }
        }

        // ========== Helper Methods for RelativeViewModel (fulfilling ISessionDataProvider contract) ==========

        /// <summary>
        /// Determines if the current session should use fastest lap positioning
        /// </summary>
        public bool ShouldUseFastestLapPositioning()
        {
            int currentSession = CurrentSessionNum;

            // Practice sessions always use fastest lap positioning if data is available
            if (IsPracticeSession(currentSession))
            {
                var resultsPositions = GetCurrentSessionResultsPositions();
                return resultsPositions.Count > 0;
            }

            // Non-lone qualifying sessions use fastest lap positioning
            if (IsQualifyingSession(currentSession) && !IsLoneQualifying(currentSession))
            {
                var fastestLaps = GetCurrentSessionFastestLaps();
                return fastestLaps.Count > 0;
            }

            // Race sessions always use race position
            return false;
        }

        /// <summary>
        /// Determines if the relative display should be hidden (e.g., lone qualifying)
        /// </summary>
        public bool ShouldHideRelativeDisplay()
        {
            int currentSession = CurrentSessionNum;
            return IsLoneQualifying(currentSession);
        }

        /// <summary>
        /// Gets fastest lap based positioning data for the current session
        /// Returns cars sorted by fastest lap time (fastest first)
        /// </summary>
        public List<(int carIdx, float fastestTime, int position)> GetFastestLapPositioning()
        {
            var result = new List<(int carIdx, float fastestTime, int position)>();
            int currentSession = CurrentSessionNum;

            if (IsPracticeSession(currentSession))
            {
                // Use ResultsPositions data (already sorted by fastest lap in practice)
                var resultsPositions = GetCurrentSessionResultsPositions();
                foreach (var pos in resultsPositions)
                {
                    if (pos.FastestTime > 0) // Only include cars with valid times
                        result.Add((pos.CarIdx, pos.FastestTime, pos.Position));
                }
            }
            else if (IsQualifyingSession(currentSession))
            {
                // Use ResultsFastestLap data and sort manually
                var fastestLaps = GetCurrentSessionFastestLaps();
                var validTimes = new List<(int carIdx, float time)>();

                foreach (var lap in fastestLaps)
                {
                    if (lap.FastestTime > 0) // Only include valid times
                        validTimes.Add((lap.CarIdx, lap.FastestTime));
                }

                // Sort by fastest time (ascending - fastest first)
                validTimes.Sort((a, b) => a.time.CompareTo(b.time));

                // Assign positions
                for (int i = 0; i < validTimes.Count; i++)
                {
                    result.Add((validTimes[i].carIdx, validTimes[i].time, i + 1));
                }
            }

            return result;
        }

        // ========== Core Parsing Methods ==========

        public bool ParseSessionData(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return false;

            try
            {
                lock (_parseLock)
                {
                    // Check if data has changed
                    var currentHash = sessionData.GetHashCode().ToString();
                    if (currentHash == _lastDataHash)
                        return false;

                    var lines = sessionData.Split('\n');

                    // Parse in order: static first, then transition, then live
                    bool staticSuccess = _staticParser.ParseStaticData(lines, _staticData);
                    bool transitionSuccess = _transitionParser.ParseTransitionData(lines, _transitionData, _staticData);
                    bool liveSuccess = _liveParser.ParseLiveData(lines, _liveData, _transitionData.CurrentSessionNum);

                    if (staticSuccess && transitionSuccess)
                    {
                        _lastDataHash = currentHash;
                        _cachedSessionYaml = sessionData; // Cache the raw YAML

                        // Update cached arrays now that new data has been parsed
                        UpdateDriverDataCaches();

                        IsDataReady = true;

                        // Debug output
                        Console.WriteLine($"[SessionCoordinator] Parsed: {_staticData.Drivers.Count} drivers, " +
                                        $"Session {_transitionData.CurrentSessionNum} ({GetCurrentSessionType()}), " +
                                        $"Track: {GetTrackDisplayName()} ({GetTrackLength():F1}m), " +
                                        $"ShouldUseFastestLap: {ShouldUseFastestLapPositioning()}");

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SessionCoordinator] Parse error: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Updates the cached driver data arrays. Called only when session data changes.
        /// </summary>
        private void UpdateDriverDataCaches()
        {
            // Clear old data to handle drivers leaving the session
            Array.Fill(_userNamesCache, null);
            Array.Fill(_carNumbersCache, null);
            Array.Fill(_carNumberRawCache, 0);
            Array.Fill(_carClassIDsCache, 0);
            Array.Fill(_carIsAICache, false);
            Array.Fill(_curDriverIncidentCountCache, 0);

            // Populate static data from the parsed models
            foreach (var kvp in _staticData.Drivers)
            {
                if (kvp.Key >= 0 && kvp.Key < 64)
                {
                    _userNamesCache[kvp.Key] = kvp.Value.UserName;
                    _carNumbersCache[kvp.Key] = kvp.Value.CarNumber;
                    _carNumberRawCache[kvp.Key] = kvp.Value.CarNumberRaw;
                    _carClassIDsCache[kvp.Key] = kvp.Value.CarClassID;
                    _carIsAICache[kvp.Key] = kvp.Value.IsAI;
                }
            }

            // Populate transition data from the parsed models
            foreach (var kvp in _transitionData.DriverIncidentCounts)
            {
                if (kvp.Key >= 0 && kvp.Key < 64)
                {
                    _curDriverIncidentCountCache[kvp.Key] = kvp.Value;
                }
            }
        }

        public void ClearCache()
        {
            lock (_parseLock)
            {
                _staticData.Drivers.Clear();
                _staticData.Schedule.Sessions.Clear();
                _staticData.IncidentLimit = 0;

                // Clear weekend/track info
                _staticData.Weekend.TrackName = string.Empty;
                _staticData.Weekend.TrackConfig = string.Empty;
                _staticData.Weekend.TrackLength = 0f;
                _staticData.Weekend.TrackDisplayName = string.Empty;
                _staticData.Weekend.TrackDisplayShortName = string.Empty;

                _transitionData.DriverIncidentCounts.Clear();
                _transitionData.CurrentSessionNum = -1;
                _transitionData.CurrentSessionType = string.Empty;
                _transitionData.CurrentSessionName = string.Empty;
                _liveData.SessionResultsPositions.Clear();
                _liveData.SessionFastestLaps.Clear();
                _liveData.QualifyPositions.Clear();
                _liveData.QualifyFastestTimes.Clear();
                _lastDataHash = string.Empty;
                IsDataReady = false;

                // Clear the cached arrays as well
                UpdateDriverDataCaches();
            }
        }

        private string _cachedSessionYaml = string.Empty;

        public string GetCachedSessionYaml()
        {
            lock (_parseLock)
            {
                return _cachedSessionYaml;
            }
        }

        public bool HasSessionDataChanged(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return false;

            lock (_parseLock)
            {
                var currentHash = sessionData.GetHashCode().ToString();
                return currentHash != _lastDataHash;
            }
        }
    }
}