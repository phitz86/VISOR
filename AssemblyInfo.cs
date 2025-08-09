using SVappsLAB.iRacingTelemetrySDK;

namespace GRiD.Wrappers
{
    // Define the telemetry variables we want to track on a class
    // This will generate a TelemetryData struct via source generation
    [RequiredTelemetryVars([
    // Lap timing
    "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
    "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",

    // Fuel and car state
    "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",

    // Positioning and relative data
    "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
    "CarIdxLap", "CarIdxLastLapTime",

    // Session info
    "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
    "SessionLapsTotal", "SessionNum",

    // Environmental
    "TrackTemp", "AirTemp", "Skies", "WindVel"
])]
    public partial class TelemetryDataDefinition
    {
        // This class exists solely to host the RequiredTelemetryVars attribute
        // The source generator will create the TelemetryData struct based on this attribute
    }
}