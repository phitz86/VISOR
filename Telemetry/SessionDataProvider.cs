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
        bool[] CarIsAI { get; }
        int[] CurDriverIncidentCount { get; }
        int IncidentLimit { get; }

        // Session-aware helper methods for positioning logic
        bool ShouldUseFastestLapPositioning();
        bool ShouldHideRelativeDisplay();
        List<(int carIdx, float fastestTime, int position)> GetFastestLapPositioning();
    }
}