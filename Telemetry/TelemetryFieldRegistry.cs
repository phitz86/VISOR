using System;
using System.Collections.Generic;

namespace VISOR.Telemetry
{
    // Central registry for all telemetry field definitions and configurations for SVappsLAB SDK
    public static class TelemetryFieldRegistry
    {
        #region Field Definitions

        // Required fields that come directly from telemetry data
        public static readonly HashSet<string> RequiredTelemetryFields = new()
        {
            // Lap timing
            "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
            "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
            
            // Fuel and car state
            "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
            
            // Positioning arrays
            "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
            "CarIdxLap", "CarIdxLastLapTime", "CarIdxOnPitRoad",
            
            // Session info - EXPANDED
            "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
            "SessionLapsTotal", "SessionNum", "PlayerCarIdx", "SessionFlags",
            
            // Environmental
            "TrackTemp", "AirTemp", "Skies", "WindVel",
            
            // NEW: Radar support fields
            "CarLeftRight", "CarIdxF2Time", "TrackLength"
        };

        // Fields that are parsed from YAML session data and added to telemetry dictionary
        public static readonly HashSet<string> YamlOnlyFields = new()
        {
            "CarIdxUserName", "CarIdxCarNumber", "CarIdxCarNumberRaw",
            "CarIdxClassID", "CarIdxIsAI", "CarIdxIncidentCount",
            
            // Session-specific YAML fields - NEW
            "SessionType", "SessionName", "QualifyResultsPositions", "QualifyResultsFastestTimes"
        };

        // Field type mappings for all supported telemetry fields
        public static readonly Dictionary<string, Type> FieldTypes = new()
        {
            // Lap timing
            ["LapCurrentLapTime"] = typeof(float),
            ["LapLastLapTime"] = typeof(float),
            ["LapBestLapTime"] = typeof(float),
            ["LapDeltaToBestLap"] = typeof(float),
            ["LapDeltaToOptimalLap"] = typeof(float),
            ["LapDeltaToSessionBestLap"] = typeof(float),
            ["Lap"] = typeof(int),

            // Fuel and car state
            ["FuelLevel"] = typeof(float),
            ["FuelUsePerHour"] = typeof(float),
            ["Gear"] = typeof(int),
            ["Speed"] = typeof(float),
            ["RPM"] = typeof(float),

            // Positioning arrays
            ["CarIdxLapDistPct"] = typeof(float[]),
            ["CarIdxPosition"] = typeof(int[]),
            ["CarIdxClassPosition"] = typeof(int[]),
            ["CarIdxTrackSurface"] = typeof(int[]),
            ["CarIdxLap"] = typeof(int[]),
            ["CarIdxLastLapTime"] = typeof(float[]),
            ["CarIdxOnPitRoad"] = typeof(bool[]),

            // Session info - EXPANDED
            ["SessionState"] = typeof(int),
            ["SessionTime"] = typeof(double),
            ["SessionTimeRemain"] = typeof(double),
            ["SessionLapsRemain"] = typeof(int),
            ["SessionLapsTotal"] = typeof(int),
            ["SessionNum"] = typeof(int),
            ["PlayerCarIdx"] = typeof(int),
            ["SessionFlags"] = typeof(int), // NEW - session flags enum

            // Environmental
            ["TrackTemp"] = typeof(float),
            ["AirTemp"] = typeof(float),
            ["Skies"] = typeof(int),
            ["WindVel"] = typeof(float),

            // NEW: Radar support fields
            ["CarLeftRight"] = typeof(float[]),
            ["CarIdxF2Time"] = typeof(float[]),
            ["TrackLength"] = typeof(float),

            // YAML-parsed fields (updated field names and types)
            ["CarIdxUserName"] = typeof(string[]),
            ["CarIdxCarNumber"] = typeof(string[]),
            ["CarIdxCarNumberRaw"] = typeof(int[]),
            ["CarIdxClassID"] = typeof(int[]),
            ["CarIdxIsAI"] = typeof(bool[]),
            ["CarIdxIncidentCount"] = typeof(int[]),

            // Session-specific YAML fields
            ["SessionType"] = typeof(string), // "Race", "Practice", "Qualifying"
            ["SessionName"] = typeof(string), // "RACE", "PRACTICE", "QUALIFY"
            ["QualifyResultsPositions"] = typeof(int[]), // Position by CarIdx
            ["QualifyResultsFastestTimes"] = typeof(float[]), // Fastest time by CarIdx

            // NEW: Track information from YAML
            ["TrackLength"] = typeof(float) // Track length in meters
        };

        #endregion

        #region Public Methods

        // Get all supported field names (both telemetry and YAML fields)
        public static HashSet<string> GetAllSupportedFields()
        {
            var allFields = new HashSet<string>(RequiredTelemetryFields);
            foreach (var field in YamlOnlyFields)
            {
                allFields.Add(field);
            }
            return allFields;
        }

        // Check if a field is supported by the telemetry system
        public static bool IsFieldSupported(string fieldName)
        {
            return RequiredTelemetryFields.Contains(fieldName) || YamlOnlyFields.Contains(fieldName);
        }

        // Get the type for a specific field
        public static Type GetFieldType(string fieldName)
        {
            return FieldTypes.TryGetValue(fieldName, out var type) ? type : null;
        }

        // Check if a field comes from telemetry data (as opposed to YAML parsing)
        public static bool IsTelemetryField(string fieldName)
        {
            return RequiredTelemetryFields.Contains(fieldName);
        }

        // Check if a field comes from YAML session data parsing
        public static bool IsYamlField(string fieldName)
        {
            return YamlOnlyFields.Contains(fieldName);
        }

        // Get all telemetry field names (excluding YAML fields)
        public static HashSet<string> GetTelemetryFields()
        {
            return new HashSet<string>(RequiredTelemetryFields);
        }

        // Get all YAML field names (excluding telemetry fields)
        public static HashSet<string> GetYamlFields()
        {
            return new HashSet<string>(YamlOnlyFields);
        }

        // Get count of all supported fields
        public static int GetSupportedFieldCount()
        {
            return RequiredTelemetryFields.Count + YamlOnlyFields.Count;
        }

        #endregion

        #region Field Categories (for UI integration)

        // Get fields related to lap timing
        public static HashSet<string> GetLapTimingFields()
        {
            return new HashSet<string>
            {
                "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime",
                "LapDeltaToBestLap", "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap"
            };
        }

        // Get fields related to car state
        public static HashSet<string> GetCarStateFields()
        {
            return new HashSet<string>
            {
                "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM"
            };
        }

        // Get fields related to car positioning (arrays)
        public static HashSet<string> GetPositioningFields()
        {
            return new HashSet<string>
            {
                "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition",
                "CarIdxTrackSurface", "CarIdxLap", "CarIdxLastLapTime", "CarIdxOnPitRoad",
                "CarIdxUserName", "CarIdxCarNumber", "CarIdxCarNumberRaw",
                "CarIdxClassID", "CarIdxIsAI", "CarIdxIncidentCount"
            };
        }

        // Get fields related to session information - EXPANDED
        public static HashSet<string> GetSessionFields()
        {
            return new HashSet<string>
            {
                "SessionState", "SessionTime", "SessionTimeRemain",
                "SessionLapsRemain", "SessionLapsTotal", "SessionNum", "PlayerCarIdx",
                "SessionFlags", "SessionType", "SessionName",
                "QualifyResultsPositions", "QualifyResultsFastestTimes"
            };
        }

        // Get fields related to environmental conditions
        public static HashSet<string> GetEnvironmentalFields()
        {
            return new HashSet<string>
            {
                "TrackTemp", "AirTemp", "Skies", "WindVel"
            };
        }

        // NEW: Get fields related to radar functionality
        public static HashSet<string> GetRadarFields()
        {
            return new HashSet<string>
            {
                "CarLeftRight", "CarIdxF2Time", "TrackLength", "CarIdxLapDistPct", "CarIdxOnPitRoad"
            };
        }

        #endregion
    }
}