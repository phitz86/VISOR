namespace VISOR.Telemetry
{
    /// <summary>
    /// One update of iRacing's session info: drivers and classes, the session schedule with its
    /// results, qualifying results and the track. Read-only once built.
    ///
    /// <see cref="SessionDataCoordinator"/> builds a new view for every update that reads
    /// correctly and publishes it with a single reference write. A frame that takes
    /// <see cref="SessionDataCoordinator.Current"/> once therefore reads one consistent session
    /// from start to finish, with no lock, even if a newer update arrives part-way through.
    ///
    /// The per-car arrays are <see cref="CarCount"/> long, indexed by CarIdx, and shared by
    /// everyone holding this view: read them, never write to them. A slot with no driver has a
    /// null name and number and zeros elsewhere.
    /// </summary>
    public sealed class SessionView : ISessionDataProvider
    {
        public const int CarCount = PositionFrame.CarCount;

        /// <summary>No session: before the first update, and after a disconnect.</summary>
        public static readonly SessionView Empty = new(
            isDataReady: false, driverCount: 0,
            userNames: new string[CarCount], carNumbers: new string[CarCount], carNumberRaw: new int[CarCount],
            carClassIDs: new int[CarCount], carClassColors: new int[CarCount], carIsAI: new bool[CarCount],
            carClassEstLapTimes: new float[CarCount], curDriverIncidentCount: new int[CarCount],
            incidentLimit: 0, subSessionId: string.Empty, playerCar: null, track: TrackInfo.None,
            sessions: new Dictionary<int, SessionDefinition>(), currentSessionNum: -1,
            qualifyPositions: new int[CarCount], qualifyFastestTimes: new float[CarCount]);

        internal SessionView(
            bool isDataReady, int driverCount,
            string[] userNames, string[] carNumbers, int[] carNumberRaw, int[] carClassIDs, int[] carClassColors,
            bool[] carIsAI, float[] carClassEstLapTimes, int[] curDriverIncidentCount,
            int incidentLimit, string subSessionId, PlayerCarInfo? playerCar, TrackInfo track,
            IReadOnlyDictionary<int, SessionDefinition> sessions, int currentSessionNum,
            int[] qualifyPositions, float[] qualifyFastestTimes)
        {
            IsDataReady = isDataReady;
            DriverCount = driverCount;
            UserNames = userNames;
            CarNumbers = carNumbers;
            CarNumberRaw = carNumberRaw;
            CarClassIDs = carClassIDs;
            CarClassColors = carClassColors;
            CarIsAI = carIsAI;
            CarClassEstLapTimes = carClassEstLapTimes;
            CurDriverIncidentCount = curDriverIncidentCount;
            IncidentLimit = incidentLimit;
            SubSessionId = subSessionId;
            PlayerCar = playerCar;
            Track = track;
            Sessions = sessions;
            CurrentSessionNum = currentSessionNum;
            QualifyPositions = qualifyPositions;
            QualifyFastestTimes = qualifyFastestTimes;

            // Worked out once per update rather than on every read (C4).
            CurrentSession = sessions.GetValueOrDefault(currentSessionNum);
            _useFastestLapPositioning = CurrentSession is { IsRace: false } s && (s.IsPractice || s.IsQualifying);
            IsLoneQualifying = CurrentSession?.IsLoneQualifying ?? false;
            FastestLapPositions = BuildFastestLapPositions(CurrentSession);
        }

        /// <summary>The update had drivers and sessions. Only such views are published.</summary>
        public bool IsDataReady { get; }

        /// <summary>Drivers in the session's driver list, the pace car included.</summary>
        public int DriverCount { get; }

        public string[] UserNames { get; }
        public string[] CarNumbers { get; }
        public int[] CarNumberRaw { get; }
        public int[] CarClassIDs { get; }
        public int[] CarClassColors { get; }
        public bool[] CarIsAI { get; }
        public float[] CarClassEstLapTimes { get; }
        public int[] CurDriverIncidentCount { get; }

        /// <summary>The session's incident limit; 0 when unlimited.</summary>
        public int IncidentLimit { get; }

        /// <summary>
        /// Stable identifier for the current iRacing session (WeekendInfo.SubSessionID). Changes
        /// whenever the player moves to a genuinely different session, even if SessionNum repeats.
        /// </summary>
        public string SubSessionId { get; }

        /// <summary>The player's car (shift-light RPMs, redline, CarPath, version), if listed.</summary>
        public PlayerCarInfo? PlayerCar { get; }

        public TrackInfo Track { get; }

        /// <summary>Every session in this update's schedule, by SessionNum.</summary>
        public IReadOnlyDictionary<int, SessionDefinition> Sessions { get; }

        public int CurrentSessionNum { get; }

        /// <summary>The current session's entry in <see cref="Sessions"/>, if it has one.</summary>
        public SessionDefinition? CurrentSession { get; }

        /// <summary>
        /// The current session is lone qualifying: the player is alone on track, so the relative
        /// and the radar hide.
        /// </summary>
        public bool IsLoneQualifying { get; }

        /// <summary>
        /// Qualifying grid positions by CarIdx (field-wide, monotonic within a class), 1-based: the
        /// pole-sitter is 1 and 0 means "this car has no qualifying result".
        /// </summary>
        public int[] QualifyPositions { get; }

        /// <summary>Qualifying fastest lap by CarIdx, in seconds; 0 when the car has none.</summary>
        public float[] QualifyFastestTimes { get; }

        /// <summary>
        /// Practice and qualifying order, from the current session's results: one entry per car
        /// with a timed lap, class and overall positions both 1-based. Empty in other sessions.
        /// </summary>
        public IReadOnlyList<(int carIdx, float fastestTime, int classPosition, int overallPosition)> FastestLapPositions { get; }

        private readonly bool _useFastestLapPositioning;

        /// <summary>
        /// Practice and qualifying order cars by fastest lap, from the session results; races,
        /// and anything else (warmup, testing), by position on track.
        /// </summary>
        public bool ShouldUseFastestLapPositioning() => _useFastestLapPositioning;

        public int[] GetQualifyResultsPositions() => QualifyPositions;

        /// <summary>Laps in the given session, or -1 when unlimited or not in the schedule.</summary>
        public int GetSessionLaps(int sessionNum) =>
            Sessions.TryGetValue(sessionNum, out var session) ? session.SessionLaps : -1;

        /// <summary>The given session's length in seconds, or 0 when not in the schedule.</summary>
        public double GetSessionTimeSeconds(int sessionNum) =>
            Sessions.TryGetValue(sessionNum, out var session) ? session.SessionTimeSeconds : 0.0;

        public bool IsQualifyingSession(int sessionNum) =>
            Sessions.TryGetValue(sessionNum, out var session) && session.IsQualifying;

        private static IReadOnlyList<(int carIdx, float fastestTime, int classPosition, int overallPosition)> BuildFastestLapPositions(SessionDefinition? session)
        {
            if (session == null || !(session.IsPractice || session.IsQualifying))
                return Array.Empty<(int carIdx, float fastestTime, int classPosition, int overallPosition)>();

            var result = new List<(int carIdx, float fastestTime, int classPosition, int overallPosition)>();
            foreach (var pos in session.ResultsPositions)
            {
                if (pos.FastestTime > 0)
                {
                    // ClassPosition is 0-based in the results YAML (needs +1); Position is
                    // already 1-based, so it is used as-is.
                    result.Add((pos.CarIdx, pos.FastestTime, pos.ClassPosition + 1, pos.Position));
                }
            }
            return result;
        }
    }

    /// <summary>The track, from WeekendInfo. LengthMeters is 0 when iRacing didn't give one.</summary>
    public sealed record TrackInfo(
        string Name,
        string Config,
        string DisplayName,
        string DisplayShortName,
        float LengthMeters,
        string EventType)
    {
        public static readonly TrackInfo None = new(string.Empty, string.Empty, string.Empty, string.Empty, 0f, string.Empty);
    }

    /// <summary>
    /// One session in the schedule, with its results so far. The session type is classified
    /// once, here, by the same substring matching VISOR has always used: "Lone Qualify" is also
    /// qualifying and "Heat Race" a race, while Warmup and Testing are none of these.
    /// </summary>
    public sealed class SessionDefinition
    {
        public SessionDefinition(int sessionNum, string sessionType, string sessionName, int sessionLaps,
            double sessionTimeSeconds, IReadOnlyList<ResultPosition> resultsPositions, IReadOnlyList<FastestLapResult> fastestLaps)
        {
            SessionNum = sessionNum;
            SessionType = sessionType;
            SessionName = sessionName;
            SessionLaps = sessionLaps;
            SessionTimeSeconds = sessionTimeSeconds;
            ResultsPositions = resultsPositions;
            FastestLaps = fastestLaps;

            IsPractice = sessionType.Contains("Practice", StringComparison.OrdinalIgnoreCase);
            IsQualifying = sessionType.Contains("Qualify", StringComparison.OrdinalIgnoreCase);
            IsLoneQualifying = sessionType.Contains("Lone", StringComparison.OrdinalIgnoreCase);
            IsRace = sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase);
        }

        public int SessionNum { get; }
        public string SessionType { get; }
        public string SessionName { get; }

        /// <summary>Laps, or -1 when unlimited.</summary>
        public int SessionLaps { get; }

        public double SessionTimeSeconds { get; }
        public IReadOnlyList<ResultPosition> ResultsPositions { get; }
        public IReadOnlyList<FastestLapResult> FastestLaps { get; }

        public bool IsPractice { get; }
        public bool IsQualifying { get; }
        public bool IsLoneQualifying { get; }
        public bool IsRace { get; }
    }

    /// <summary>A results entry. Position is 1-based, ClassPosition 0-based, as iRacing writes them.</summary>
    public sealed record ResultPosition(int Position, int ClassPosition, int CarIdx, int Lap, float Time, float FastestTime, float LastTime);

    public sealed record FastestLapResult(int CarIdx, int FastestLap, float FastestTime);
}
