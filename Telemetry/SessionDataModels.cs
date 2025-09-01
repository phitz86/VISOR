using System.Collections.Generic;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Static event data that never changes during an event
    /// </summary>
    public class StaticEventData
    {
        public readonly Dictionary<int, DriverInfo> Drivers = new();
        public readonly SessionSchedule Schedule = new();
        public int IncidentLimit { get; set; }

        public class DriverInfo
        {
            public string UserName { get; set; } = string.Empty;
            public string CarNumber { get; set; } = string.Empty;
            public int CarNumberRaw { get; set; }
            public int CarClassID { get; set; }
            public bool IsAI { get; set; }
        }

        public class SessionSchedule
        {
            public readonly Dictionary<int, SessionDefinition> Sessions = new();

            public class SessionDefinition
            {
                public int SessionNum { get; set; }
                public string SessionType { get; set; } = string.Empty;
                public string SessionName { get; set; } = string.Empty;
                public int SessionLaps { get; set; } = -1; // -1 = unlimited
                public double SessionTimeSeconds { get; set; }
            }
        }
    }

    /// <summary>
    /// Session-specific data that changes between sessions
    /// </summary>
    public class SessionTransitionData
    {
        public int CurrentSessionNum { get; set; } = -1;
        public readonly Dictionary<int, int> DriverIncidentCounts = new(); // CarIdx -> Count

        // Legacy compatibility
        public string CurrentSessionType { get; set; } = string.Empty;
        public string CurrentSessionName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Live session data that updates frequently during sessions
    /// </summary>
    public class LiveSessionData
    {
        // Results by session number
        public readonly Dictionary<int, List<ResultPosition>> SessionResultsPositions = new();
        public readonly Dictionary<int, List<FastestLapResult>> SessionFastestLaps = new();

        // Qualifying results (legacy)
        public readonly Dictionary<int, int> QualifyPositions = new(); // CarIdx -> Position
        public readonly Dictionary<int, float> QualifyFastestTimes = new(); // CarIdx -> Time

        public class ResultPosition
        {
            public int Position { get; set; }
            public int ClassPosition { get; set; }
            public int CarIdx { get; set; }
            public int Lap { get; set; }
            public float Time { get; set; }
            public float FastestTime { get; set; }
            public float LastTime { get; set; }
        }

        public class FastestLapResult
        {
            public int CarIdx { get; set; }
            public int FastestLap { get; set; }
            public float FastestTime { get; set; }
        }
    }
}