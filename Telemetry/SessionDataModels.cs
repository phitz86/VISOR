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

        /// <summary>CarIdxTrackSurface for an empty car slot, or a car not on the track at all.</summary>
        public const int TrackSurfaceNotInWorld = -1;
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
}
