using System.Collections.Generic;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Defines the contract for providing parsed iRacing session data.
    /// </summary>
    public interface ISessionDataProvider
    {
        bool IsDataReady { get; }
        string[] UserNames { get; }
        string[] CarNumbers { get; }
        int[] CarNumberRaw { get; }
        int[] CarClassIDs { get; }
        int[] CarClassColors { get; }
        bool[] CarIsAI { get; }
        float[] CarClassEstLapTimes { get; }
        int[] CurDriverIncidentCount { get; }
        int IncidentLimit { get; }
        int CurrentSessionNum { get; }

        // Stable identifier for the current iRacing session (from WeekendInfo.SubSessionID). Changes
        // whenever the player moves to a genuinely different session, even if SessionNum repeats.
        string SubSessionId { get; }

        // Session definition queries
        int GetSessionLaps(int sessionNum);
        double GetSessionTimeSeconds(int sessionNum);

        bool IsQualifyingSession(int sessionNum);

        // Session-aware helper methods for positioning logic
        bool ShouldUseFastestLapPositioning();
        bool ShouldHideRelativeDisplay();
        List<(int carIdx, float fastestTime, int classPosition, int overallPosition)> GetFastestLapPositioning();

        // Qualifying results for reference lap time cascade
        float[] GetQualifyResultsFastestTimes();

        // Qualifying grid positions (field-wide, monotonic within a class).
        // Used as a fallback grid order for cars that have not yet taken the green flag.
        int[] GetQualifyResultsPositions();
    }
}