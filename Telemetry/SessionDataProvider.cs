namespace VISOR.Telemetry
{
    /// <summary>
    /// The session info the race logic (VISOR.Race) reads: the driver list, car classes, which
    /// positioning mode the session uses and the qualifying grid. <see cref="SessionView"/>
    /// implements it; the tests use a hand-filled fake. Everything else reads a SessionView.
    /// </summary>
    public interface ISessionDataProvider
    {
        bool IsDataReady { get; }

        // Per car, by CarIdx, CarCount (64) long. Read only.
        string[] UserNames { get; }
        string[] CarNumbers { get; }
        int[] CarClassIDs { get; }

        // Practice and qualifying order cars by fastest lap instead of position on track.
        bool ShouldUseFastestLapPositioning();

        // Qualifying grid positions (field-wide, monotonic within a class), 1-based: the
        // pole-sitter is 1 and 0 means "this car has no qualifying result". Used as a fallback
        // grid order for cars that have not yet taken the green flag. Read only.
        int[] GetQualifyResultsPositions();
    }
}
