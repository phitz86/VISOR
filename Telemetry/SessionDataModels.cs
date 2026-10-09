using System;
using System.Collections.Generic;

namespace VISOR.Telemetry
{
    [Flags]
    public enum SessionFlags
    {
        None = 0,
        Checkered = 0x00000001,
        White = 0x00000002,
        Green = 0x00000004,
        Yellow = 0x00000008,
        Red = 0x00000010,
        Blue = 0x00000020,
        Debris = 0x00000040,
        Crossed = 0x00000080,
        YellowWaving = 0x00000100,
        OneLapToGreen = 0x00000200,
        GreenHeld = 0x00000400,
        TenToGo = 0x00000800,
        FiveToGo = 0x00001000,
        RandomWaving = 0x00002000,
        Caution = 0x00004000,
        CautionWaving = 0x00008000,
    }

    /// <summary>
    /// iRacing's SessionState values (irsdk_SessionState). The telemetry field is a plain int.
    /// </summary>
    public static class SessionStates
    {
        public const int Invalid = 0;
        public const int GetInCar = 1;
        public const int Warmup = 2;
        public const int ParadeLaps = 3;
        public const int Racing = 4;
        public const int Checkered = 5;
        public const int CoolDown = 6;

        /// <summary>The checkered flag is out (Checkered, then CoolDown).</summary>
        public static bool IsCheckered(int state) => state == Checkered || state == CoolDown;

        public static string Name(int state) => state switch
        {
            Invalid => "Invalid",
            GetInCar => "GetInCar",
            Warmup => "Warmup",
            ParadeLaps => "ParadeLaps",
            Racing => "Racing",
            Checkered => "Checkered",
            CoolDown => "CoolDown",
            _ => $"Unknown({state})"
        };
    }

    /// <summary>Fixed iRacing identifiers and masks that several parts of VISOR test against.</summary>
    public static class IRacingIds
    {
        /// <summary>The pace/safety car's car class; it's left out of field positions.</summary>
        public const int PaceCarClassId = 11;

        /// <summary>The SessionFlags bits that bear on the finish: checkered, white and green.</summary>
        public const int FinishFlagsMask = (int)(SessionFlags.Checkered | SessionFlags.White | SessionFlags.Green);
    }

    /// <summary>
    /// Static event data that never changes during an event
    /// </summary>
    public class StaticEventData
    {
        public readonly Dictionary<int, DriverInfo> Drivers = new();
        public readonly SessionSchedule Schedule = new();
        public readonly WeekendInfo Weekend = new();
        public int IncidentLimit { get; set; }
        public PlayerCarInfo? PlayerCar { get; set; }

        public class DriverInfo
        {
            public string UserName { get; set; } = string.Empty;
            public string CarNumber { get; set; } = string.Empty;
            public int CarNumberRaw { get; set; }
            public int CarClassID { get; set; }
            public int CarClassColor { get; set; }
            public bool IsAI { get; set; }

            public float CarClassEstLapTime { get; set; }
        }

        public class SessionSchedule
        {
            public readonly Dictionary<int, SessionDefinition> Sessions = new();

            public class SessionDefinition
            {
                public int SessionNum { get; set; }
                public string SessionType { get; set; } = string.Empty;
                public string SessionName { get; set; } = string.Empty;
                public int SessionLaps { get; set; } = -1;
                public double SessionTimeSeconds { get; set; }
            }
        }

        public class WeekendInfo
        {
            public string TrackName { get; set; } = string.Empty;
            public string TrackConfig { get; set; } = string.Empty;
            public float TrackLength { get; set; } = 0f;
            public string TrackDisplayName { get; set; } = string.Empty;
            public string TrackDisplayShortName { get; set; } = string.Empty;

            // EventType ("Practice"/"Race"/...) and SubSessionID uniquely (the latter) identify the
            // iRacing session you're in. A change in either signals a genuinely new session even when
            // SessionNum is unchanged (e.g. open practice -> a race weekend's practice).
            public string EventType { get; set; } = string.Empty;
            public string SubSessionId { get; set; } = string.Empty;
        }
    }

    /// <summary>
    /// The player's car as described by the session YAML (DriverInfo + the player's Drivers
    /// entry). Immutable: a new instance is published on every session-info parse, so the 60Hz
    /// path can read it without a lock.
    /// </summary>
    public sealed record PlayerCarInfo(
        string CarPath,
        string CarScreenName,
        string CarVersion,
        float RedLine,
        float SLFirstRPM,
        float SLShiftRPM,
        int GearNumForward,
        float SLLastRPM = 0f,
        float SLBlinkRPM = 0f);

    public class SessionTransitionData
    {
        public int CurrentSessionNum { get; set; } = -1;
        public readonly Dictionary<int, int> DriverIncidentCounts = new();

        public string CurrentSessionType { get; set; } = string.Empty;
        public string CurrentSessionName { get; set; } = string.Empty;
    }

    public class LiveSessionData
    {
        public readonly Dictionary<int, List<ResultPosition>> SessionResultsPositions = new();
        public readonly Dictionary<int, List<FastestLapResult>> SessionFastestLaps = new();

        public readonly Dictionary<int, int> QualifyPositions = new();
        public readonly Dictionary<int, float> QualifyFastestTimes = new();

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