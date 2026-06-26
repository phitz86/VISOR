using System;
using System.Collections.Generic;
using System.Linq;
using SVappsLAB.iRacingTelemetrySDK;
using VISOR.Diagnostics;

namespace VISOR.Telemetry
{
    public class SessionDataCoordinator : ISessionDataProvider
    {
        private readonly StaticEventData _staticData = new();
        private readonly SessionTransitionData _transitionData = new();
        private readonly LiveSessionData _liveData = new();

        private readonly object _parseLock = new();

        public bool IsDataReady { get; private set; }

        // Cached arrays are replaced atomically (reference assignment is atomic in C#) so the 60Hz
        // telemetry read path can access them without a lock.
        private volatile string[] _userNamesCache = new string[64];
        private volatile string[] _carNumbersCache = new string[64];
        private volatile int[] _carNumberRawCache = new int[64];
        private volatile int[] _carClassIDsCache = new int[64];
        private volatile int[] _carClassColorsCache = new int[64];
        private volatile bool[] _carIsAICache = new bool[64];
        private volatile float[] _carClassEstLapTimesCache = new float[64];
        private volatile int[] _curDriverIncidentCountCache = new int[64];

        public string[] UserNames => _userNamesCache;
        public string[] CarNumbers => _carNumbersCache;
        public int[] CarNumberRaw => _carNumberRawCache;
        public int[] CarClassIDs => _carClassIDsCache;
        public int[] CarClassColors => _carClassColorsCache;
        public bool[] CarIsAI => _carIsAICache;
        public float[] CarClassEstLapTimes => _carClassEstLapTimesCache;
        public int[] CurDriverIncidentCount => _curDriverIncidentCountCache;

        public StaticEventData GetStaticEventData()
        {
            lock (_parseLock)
            {
                return _staticData;
            }
        }

        public LiveSessionData GetLiveSessionData()
        {
            lock (_parseLock)
            {
                return _liveData;
            }
        }

        public int IncidentLimit
        {
            get { lock (_parseLock) { return _staticData.IncidentLimit; } }
        }

        public int CurrentSessionNum
        {
            get { lock (_parseLock) { return _transitionData.CurrentSessionNum; } }
        }

        public string SubSessionId
        {
            get { lock (_parseLock) { return _staticData.Weekend.SubSessionId; } }
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

        public List<(int carIdx, float fastestTime, int classPosition, int overallPosition)> GetFastestLapPositioning()
        {
            var result = new List<(int carIdx, float fastestTime, int classPosition, int overallPosition)>();
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
                        // ClassPosition is 0-based in the results YAML (needs +1); Position is
                        // already 1-based, so it is used as-is.
                        result.Add((pos.CarIdx, pos.FastestTime, pos.ClassPosition + 1, pos.Position));
                    }
                }
            }

            return result;
        }

        public bool ApplySdkSession(TelemetrySessionInfo info)
        {
            if (info == null) return false;

            try
            {
                lock (_parseLock)
                {
                    SessionDataAdapter.Apply(info, _staticData, _transitionData, _liveData);

                    bool ready = _staticData.Drivers.Count > 0 && _staticData.Schedule.Sessions.Count > 0;
                    if (!ready) return false;

                    UpdateDriverDataCaches();
                    IsDataReady = true;

                    var trackName = !string.IsNullOrEmpty(_staticData.Weekend.TrackDisplayName)
                        ? _staticData.Weekend.TrackDisplayName
                        : _staticData.Weekend.TrackName;
                    var sessions = string.Join(", ", _staticData.Schedule.Sessions.Values.Select(s => s.SessionType));
                    var summary = $"Session data parsed: {trackName}, {_staticData.Drivers.Count} drivers, sessions: [{sessions}]";
                    if (summary != _lastParseSummary)
                    {
                        Log.Info(summary);
                        _lastParseSummary = summary;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("SessionDataCoordinator ApplySdkSession error", ex);
                return false;
            }
        }

        private void UpdateDriverDataCaches()
        {
            var newUserNames = new string[64];
            var newCarNumbers = new string[64];
            var newCarNumberRaw = new int[64];
            var newCarClassIDs = new int[64];
            var newCarClassColors = new int[64];
            var newCarIsAI = new bool[64];
            var newCarClassEstLapTimes = new float[64];
            var newIncidentCounts = new int[64];

            foreach (var kvp in _staticData.Drivers)
            {
                if (kvp.Key >= 0 && kvp.Key < 64)
                {
                    newUserNames[kvp.Key] = kvp.Value.UserName;
                    newCarNumbers[kvp.Key] = kvp.Value.CarNumber;
                    newCarNumberRaw[kvp.Key] = kvp.Value.CarNumberRaw;
                    newCarClassIDs[kvp.Key] = kvp.Value.CarClassID;
                    newCarClassColors[kvp.Key] = kvp.Value.CarClassColor;
                    newCarIsAI[kvp.Key] = kvp.Value.IsAI;
                    newCarClassEstLapTimes[kvp.Key] = kvp.Value.CarClassEstLapTime;
                }
            }

            foreach (var kvp in _transitionData.DriverIncidentCounts)
            {
                if (kvp.Key >= 0 && kvp.Key < 64)
                {
                    newIncidentCounts[kvp.Key] = kvp.Value;
                }
            }

            _userNamesCache = newUserNames;
            _carNumbersCache = newCarNumbers;
            _carNumberRawCache = newCarNumberRaw;
            _carClassIDsCache = newCarClassIDs;
            _carClassColorsCache = newCarClassColors;
            _carIsAICache = newCarIsAI;
            _carClassEstLapTimesCache = newCarClassEstLapTimes;
            _curDriverIncidentCountCache = newIncidentCounts;
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
                _staticData.Weekend.EventType = string.Empty;
                _staticData.Weekend.SubSessionId = string.Empty;

                _transitionData.DriverIncidentCounts.Clear();
                _transitionData.CurrentSessionNum = -1;
                _transitionData.CurrentSessionType = string.Empty;
                _transitionData.CurrentSessionName = string.Empty;
                _liveData.SessionResultsPositions.Clear();
                _liveData.SessionFastestLaps.Clear();
                _liveData.QualifyPositions.Clear();
                _liveData.QualifyFastestTimes.Clear();
                _lastParseSummary = string.Empty;
                IsDataReady = false;

                UpdateDriverDataCaches();
            }
        }

        private string _lastParseSummary = string.Empty;
    }
}