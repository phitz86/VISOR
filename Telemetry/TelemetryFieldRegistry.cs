using System;
using System.Collections.Generic;

namespace VISOR.Telemetry
{
    public static class TelemetryFieldRegistry
    {
        #region Field Definitions

        public static readonly HashSet<string> RequiredTelemetryFields = new()
        {
            "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
            "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
            "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
            "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
            "CarIdxLap", "CarIdxLastLapTime", "CarIdxOnPitRoad", "CarIdxEstTime",
            "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
            "SessionLapsTotal", "SessionNum", "PlayerCarIdx", "SessionFlags",
            "TrackTemp", "AirTemp", "Skies", "WindVel",
            "CarLeftRight", "CarIdxF2Time", "TrackLength"
        };

        public static readonly HashSet<string> YamlOnlyFields = new()
        {
            "CarIdxUserName", "CarIdxCarNumber", "CarIdxCarNumberRaw",
            "CarIdxClassID", "CarIdxIsAI", "CarIdxIncidentCount",
            "SessionType", "SessionName", "QualifyResultsPositions", "QualifyResultsFastestTimes"
        };

        public static readonly Dictionary<string, Type> FieldTypes = new()
        {
            ["LapCurrentLapTime"] = typeof(float),
            ["LapLastLapTime"] = typeof(float),
            ["LapBestLapTime"] = typeof(float),
            ["LapDeltaToBestLap"] = typeof(float),
            ["LapDeltaToOptimalLap"] = typeof(float),
            ["LapDeltaToSessionBestLap"] = typeof(float),
            ["Lap"] = typeof(int),

            ["FuelLevel"] = typeof(float),
            ["FuelUsePerHour"] = typeof(float),
            ["Gear"] = typeof(int),
            ["Speed"] = typeof(float),
            ["RPM"] = typeof(float),

            ["CarIdxLapDistPct"] = typeof(float[]),
            ["CarIdxPosition"] = typeof(int[]),
            ["CarIdxClassPosition"] = typeof(int[]),
            ["CarIdxTrackSurface"] = typeof(int[]),
            ["CarIdxLap"] = typeof(int[]),
            ["CarIdxLastLapTime"] = typeof(float[]),
            ["CarIdxOnPitRoad"] = typeof(bool[]),
            ["CarIdxEstTime"] = typeof(float[]),

            ["SessionState"] = typeof(int),
            ["SessionTime"] = typeof(double),
            ["SessionTimeRemain"] = typeof(double),
            ["SessionLapsRemain"] = typeof(int),
            ["SessionLapsTotal"] = typeof(int),
            ["SessionNum"] = typeof(int),
            ["PlayerCarIdx"] = typeof(int),
            ["SessionFlags"] = typeof(int),

            ["TrackTemp"] = typeof(float),
            ["AirTemp"] = typeof(float),
            ["Skies"] = typeof(int),
            ["WindVel"] = typeof(float),

            ["CarLeftRight"] = typeof(int),
            ["CarIdxF2Time"] = typeof(float[]),
            ["TrackLength"] = typeof(float),

            ["CarIdxUserName"] = typeof(string[]),
            ["CarIdxCarNumber"] = typeof(string[]),
            ["CarIdxCarNumberRaw"] = typeof(int[]),
            ["CarIdxClassID"] = typeof(int[]),
            ["CarIdxIsAI"] = typeof(bool[]),
            ["CarIdxIncidentCount"] = typeof(int[]),

            ["SessionType"] = typeof(string),
            ["SessionName"] = typeof(string),
            ["QualifyResultsPositions"] = typeof(int[]),
            ["QualifyResultsFastestTimes"] = typeof(float[])
        };

        #endregion

        #region Public Methods

        public static HashSet<string> GetAllSupportedFields()
        {
            var allFields = new HashSet<string>(RequiredTelemetryFields);
            foreach (var field in YamlOnlyFields)
            {
                allFields.Add(field);
            }
            return allFields;
        }

        public static bool IsFieldSupported(string fieldName)
        {
            return RequiredTelemetryFields.Contains(fieldName) || YamlOnlyFields.Contains(fieldName);
        }

        public static Type GetFieldType(string fieldName)
        {
            return FieldTypes.TryGetValue(fieldName, out var type) ? type : null;
        }

        public static bool IsTelemetryField(string fieldName)
        {
            return RequiredTelemetryFields.Contains(fieldName);
        }

        public static bool IsYamlField(string fieldName)
        {
            return YamlOnlyFields.Contains(fieldName);
        }

        public static HashSet<string> GetTelemetryFields()
        {
            return new HashSet<string>(RequiredTelemetryFields);
        }

        public static HashSet<string> GetYamlFields()
        {
            return new HashSet<string>(YamlOnlyFields);
        }

        public static int GetSupportedFieldCount()
        {
            return RequiredTelemetryFields.Count + YamlOnlyFields.Count;
        }

        #endregion

        #region Field Categories

        public static HashSet<string> GetLapTimingFields()
        {
            return new HashSet<string>
            {
                "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime",
                "LapDeltaToBestLap", "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap"
            };
        }

        public static HashSet<string> GetCarStateFields()
        {
            return new HashSet<string>
            {
                "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM"
            };
        }

        public static HashSet<string> GetPositioningFields()
        {
            return new HashSet<string>
            {
                "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition",
                "CarIdxTrackSurface", "CarIdxLap", "CarIdxLastLapTime", "CarIdxOnPitRoad",
                "CarIdxEstTime", "CarIdxUserName", "CarIdxCarNumber", "CarIdxCarNumberRaw", "CarIdxLapCompleted",
                "CarIdxClassID", "CarIdxIsAI", "CarIdxIncidentCount"
            };
        }

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

        public static HashSet<string> GetEnvironmentalFields()
        {
            return new HashSet<string>
            {
                "TrackTemp", "AirTemp", "Skies", "WindVel"
            };
        }

        public static HashSet<string> GetRadarFields()
        {
            return new HashSet<string>
            {
                "CarLeftRight",
                "CarIdxF2Time",
                "TrackLength",
                "CarIdxLapDistPct",
                "CarIdxOnPitRoad"
            };
        }

        #endregion
    }
}