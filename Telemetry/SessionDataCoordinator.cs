using System;
using System.Collections.Generic;
using System.Linq;

namespace VISOR.Telemetry
{
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

        private readonly string[] _userNamesCache = new string[64];
        private readonly string[] _carNumbersCache = new string[64];
        private readonly int[] _carNumberRawCache = new int[64];
        private readonly int[] _carClassIDsCache = new int[64];
        private readonly int[] _carClassColorsCache = new int[64];
        private readonly bool[] _carIsAICache = new bool[64];
        private readonly int[] _curDriverIncidentCountCache = new int[64];

        public string[] UserNames
        {
            get { lock (_parseLock) { return _userNamesCache; } }
        }

        public string[] CarNumbers
        {
            get { lock (_parseLock) { return _carNumbersCache; } }
        }

        public int[] CarNumberRaw
        {
            get { lock (_parseLock) { return _carNumberRawCache; } }
        }

        public int[] CarClassIDs
        {
            get { lock (_parseLock) { return _carClassIDsCache; } }
        }

        public int[] CarClassColors
        {
            get { lock (_parseLock) { return _carClassColorsCache; } }
        }

        public bool[] CarIsAI
        {
            get { lock (_parseLock) { return _carIsAICache; } }
        }

        public int[] CurDriverIncidentCount
        {
            get { lock (_parseLock) { return _curDriverIncidentCountCache; } }
        }

        public int IncidentLimit
        {
            get { lock (_parseLock) { return _staticData.IncidentLimit; } }
        }

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

        public float GetTrackLength()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackLength;
            }
        }

        public string GetTrackName()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackName;
            }
        }

        public string GetTrackConfig()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackConfig;
            }
        }

        public string GetTrackDisplayName()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackDisplayName;
            }
        }

        public string GetTrackDisplayShortName()
        {
            lock (_parseLock)
            {
                return _staticData.Weekend.TrackDisplayShortName;
            }
        }

        public List<LiveSessionData.ResultPosition> GetCurrentSessionResultsPositions()
        {
            lock (_parseLock)
            {
                var positions = _liveData.SessionResultsPositions.GetValueOrDefault(CurrentSessionNum,
                    new List<LiveSessionData.ResultPosition>());
                return new List<LiveSessionData.ResultPosition>(positions);
            }
        }

        public List<LiveSessionData.ResultPosition> GetSessionResultsPositions(int sessionNum)
        {
            lock (_parseLock)
            {
                var positions = _liveData.SessionResultsPositions.GetValueOrDefault(sessionNum,
                    new List<LiveSessionData.ResultPosition>());
                return new List<LiveSessionData.ResultPosition>(positions);
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

        public bool ShouldUseFastestLapPositioning()
        {
            int currentSession = CurrentSessionNum;

            if (IsRaceSession(currentSession))
                return false;

            if (IsPracticeSession(currentSession) || (IsQualifyingSession(currentSession)))
            {
                return true;
            }

            return false;
        }

        public bool ShouldHideRelativeDisplay()
        {
            int currentSession = CurrentSessionNum;
            return IsLoneQualifying(currentSession);
        }

        public List<(int carIdx, float fastestTime, int position)> GetFastestLapPositioning()
        {
            var result = new List<(int carIdx, float fastestTime, int position)>();
            int currentSession = CurrentSessionNum;

            if (IsPracticeSession(currentSession) || IsQualifyingSession(currentSession))
            {
                List<LiveSessionData.ResultPosition> resultsSnapshot;
                lock (_parseLock)
                {
                    var resultsPositions = _liveData.SessionResultsPositions.GetValueOrDefault(currentSession,
                        new List<LiveSessionData.ResultPosition>());
                    resultsSnapshot = new List<LiveSessionData.ResultPosition>(resultsPositions);
                }

                foreach (var pos in resultsSnapshot)
                {
                    if (pos.FastestTime > 0)
                    {
                        result.Add((pos.CarIdx, pos.FastestTime, pos.ClassPosition + 1));
                    }
                }
            }

            return result;
        }

        public bool ParseSessionData(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return false;

            try
            {
                lock (_parseLock)
                {
                    var currentHash = sessionData.GetHashCode().ToString();

                    if (currentHash == _lastDataHash)
                    {
                        return false;
                    }

                    var lines = sessionData.Split('\n');

                    bool staticSuccess = _staticParser.ParseStaticData(lines, _staticData);
                    bool transitionSuccess = _transitionParser.ParseTransitionData(lines, _transitionData, _staticData);
                    bool liveSuccess = _liveParser.ParseLiveData(lines, _liveData, _transitionData.CurrentSessionNum);

                    if (staticSuccess && transitionSuccess)
                    {
                        _lastDataHash = currentHash;
                        _cachedSessionYaml = sessionData;

                        UpdateDriverDataCaches();

                        IsDataReady = true;

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionCoordinator] Parse error: {ex.Message}");
            }

            return false;
        }

        private void UpdateDriverDataCaches()
        {
            Array.Fill(_userNamesCache, null);
            Array.Fill(_carNumbersCache, null);
            Array.Fill(_carNumberRawCache, 0);
            Array.Fill(_carClassIDsCache, 0);
            Array.Fill(_carClassColorsCache, 0);
            Array.Fill(_carIsAICache, false);
            Array.Fill(_curDriverIncidentCountCache, 0);

            foreach (var kvp in _staticData.Drivers)
            {
                if (kvp.Key >= 0 && kvp.Key < 64)
                {
                    _userNamesCache[kvp.Key] = kvp.Value.UserName;
                    _carNumbersCache[kvp.Key] = kvp.Value.CarNumber;
                    _carNumberRawCache[kvp.Key] = kvp.Value.CarNumberRaw;
                    _carClassIDsCache[kvp.Key] = kvp.Value.CarClassID;
                    _carClassColorsCache[kvp.Key] = kvp.Value.CarClassColor;
                    _carIsAICache[kvp.Key] = kvp.Value.IsAI;
                }
            }

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