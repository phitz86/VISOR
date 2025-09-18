using System;
using System.Collections.Generic;
using SVappsLAB.iRacingTelemetrySDK;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Builds telemetry dictionaries from raw SDK data and merges with session data.
    /// Updated to work with new session data structure and field names.
    /// </summary>
    public class TelemetryDataBuilder
    {
        // MODIFIED: Changed dependency from the wrapper to the coordinator
        private readonly SessionDataCoordinator _coordinator;

        // Static fields for tracking state changes
        private static int _lastSessionState = -1;
        private static int _lastSessionFlags = -1;
        private static string _lastSessionInfo = "";

        // MODIFIED: Constructor now accepts the coordinator
        public TelemetryDataBuilder(SessionDataCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        /// <summary>
        /// Build a complete telemetry dictionary from raw telemetry data
        /// </summary>
        public Dictionary<string, object> BuildTelemetryDictionary(TelemetryData telemetryData)
        {
            var dict = new Dictionary<string, object>();

            try
            {
                // Add all telemetry fields using direct approach
                AddLapTimingData(dict, telemetryData);
                AddCarStateData(dict, telemetryData);
                AddPositioningData(dict, telemetryData);
                AddSessionData(dict, telemetryData);
                AddPlayerData(dict, telemetryData);
                AddRadarData(dict, telemetryData);

                // Merge in YAML-parsed data from the coordinator
                AddYamlData(dict);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DataBuilder] Error building telemetry dictionary: {ex.Message}");
            }

            return dict;
        }

        private void AddLapTimingData(Dictionary<string, object> dict, TelemetryData data)
        {
            dict["LapCurrentLapTime"] = data.LapCurrentLapTime;
            dict["LapLastLapTime"] = data.LapLastLapTime;
            dict["LapBestLapTime"] = data.LapBestLapTime;
            dict["LapDeltaToBestLap"] = data.LapDeltaToBestLap;
            dict["LapDeltaToOptimalLap"] = data.LapDeltaToOptimalLap;
            dict["LapDeltaToSessionBestLap"] = data.LapDeltaToSessionBestLap;
            dict["Lap"] = data.Lap;
        }

        private void AddCarStateData(Dictionary<string, object> dict, TelemetryData data)
        {
            dict["FuelLevel"] = data.FuelLevel;
            dict["FuelUsePerHour"] = data.FuelUsePerHour;
            dict["Gear"] = data.Gear;
            dict["Speed"] = data.Speed;
            dict["RPM"] = data.RPM;
        }

        private void AddPositioningData(Dictionary<string, object> dict, TelemetryData data)
        {
            // Direct array assignment from telemetry
            dict["CarIdxLapDistPct"] = data.CarIdxLapDistPct;
            dict["CarIdxPosition"] = data.CarIdxPosition;
            dict["CarIdxClassPosition"] = data.CarIdxClassPosition;
            dict["CarIdxTrackSurface"] = data.CarIdxTrackSurface;
            dict["CarIdxLap"] = data.CarIdxLap;
            dict["CarIdxLastLapTime"] = data.CarIdxLastLapTime;

            // CarIdxOnPitRoad should be available now with updated RequiredTelemetryVars
            dict["CarIdxOnPitRoad"] = SafeGetFieldValue(data, "CarIdxOnPitRoad", new bool[64]);
        }

        private void AddSessionData(Dictionary<string, object> dict, TelemetryData data)
        {
            // SessionState is an enum, need to cast to int for dictionary storage
            dict["SessionState"] = (int)data.SessionState;
            dict["SessionTime"] = data.SessionTime;
            dict["SessionTimeRemain"] = data.SessionTimeRemain;
            dict["SessionLapsRemain"] = data.SessionLapsRemain;
            dict["SessionLapsTotal"] = data.SessionLapsTotal;
            dict["SessionNum"] = data.SessionNum;

            // SessionFlags with safe access
            dict["SessionFlags"] = SafeGetFieldValue(data, "SessionFlags", 0);

            // DEBUG: Log session state changes
            int currentSessionState = (int)data.SessionState;
            if (currentSessionState != _lastSessionState)
            {
                Console.WriteLine($"[DataBuilder DEBUG] SessionState changed: {_lastSessionState} -> {currentSessionState}");
                System.Diagnostics.Debug.WriteLine($"[DataBuilder] SessionState: {_lastSessionState} -> {currentSessionState}");
                _lastSessionState = currentSessionState;
            }
        }

        private void AddPlayerData(Dictionary<string, object> dict, TelemetryData data)
        {
            dict["PlayerCarIdx"] = data.PlayerCarIdx;
        }

        private void AddRadarData(Dictionary<string, object> dict, TelemetryData data)
        {
            // Store CarLeftRight enum directly without conversion
            dict["CarLeftRight"] = SafeGetFieldValue<object>(data, "CarLeftRight", null);

            // CarIdxF2Time - gap to leader array  
            dict["CarIdxF2Time"] = SafeGetFieldValue(data, "CarIdxF2Time", new float[64]);

            // TrackLength - total track length
            dict["TrackLength"] = SafeGetFieldValue(data, "TrackLength", 0f);

            // DEBUG: Log when radar fields are available
            var carLeftRight = dict["CarLeftRight"];
            var carIdxF2Time = dict["CarIdxF2Time"] as float[];
            var trackLength = (float)dict["TrackLength"];

            if (carLeftRight != null)
            {
                System.Diagnostics.Debug.WriteLine($"[DataBuilder] CarLeftRight: {carLeftRight} ({carLeftRight.GetType().Name})");
            }

            if (carIdxF2Time != null && carIdxF2Time.Length > 0 && trackLength > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[DataBuilder] Radar data available - TrackLength: {trackLength:F1}m");
            }
        }

        private void AddYamlData(Dictionary<string, object> dict)
        {
            // MODIFIED: Get YAML-parsed arrays from the coordinator's cached properties
            dict["CarIdxUserName"] = _coordinator.UserNames;
            dict["CarIdxCarNumber"] = _coordinator.CarNumbers;
            dict["CarIdxCarNumberRaw"] = _coordinator.CarNumberRaw;
            dict["CarIdxClassID"] = _coordinator.CarClassIDs;
            dict["CarIdxIsAI"] = _coordinator.CarIsAI;
            dict["CarIdxIncidentCount"] = _coordinator.CurDriverIncidentCount;

            // MODIFIED: Get session-specific YAML data from coordinator methods
            dict["SessionType"] = _coordinator.GetCurrentSessionType();
            dict["SessionName"] = _coordinator.GetCurrentSessionName();
            dict["QualifyResultsPositions"] = _coordinator.GetQualifyResultsPositions();
            dict["QualifyResultsFastestTimes"] = _coordinator.GetQualifyResultsFastestTimes();

            // NEW: Get track information from YAML data
            dict["TrackLength"] = _coordinator.GetTrackLength();

            // DEBUG: Log session info when it's available
            var sessionType = _coordinator.GetCurrentSessionType();
            var sessionName = _coordinator.GetCurrentSessionName();
            var trackLength = _coordinator.GetTrackLength();

            if (!string.IsNullOrEmpty(sessionType))
            {
                string currentSessionInfo = $"{sessionType}({sessionName})";
                if (currentSessionInfo != _lastSessionInfo)
                {
                    Console.WriteLine($"[DataBuilder DEBUG] Session Info: {currentSessionInfo}");
                    System.Diagnostics.Debug.WriteLine($"[DataBuilder] Session Info: {currentSessionInfo}");

                    if (trackLength > 0)
                    {
                        Console.WriteLine($"[DataBuilder DEBUG] Track Length: {trackLength:F1}m");
                        System.Diagnostics.Debug.WriteLine($"[DataBuilder] Track Length: {trackLength:F1}m");
                    }

                    // Count human vs AI drivers for debug
                    var carIsAI = _coordinator.CarIsAI;
                    int humanCount = 0, aiCount = 0;
                    foreach (var isAI in carIsAI)
                    {
                        if (isAI) aiCount++; else humanCount++;
                    }
                    Console.WriteLine($"[DataBuilder DEBUG] Driver counts - Human: {humanCount}, AI: {aiCount}");
                    System.Diagnostics.Debug.WriteLine($"[DataBuilder] Drivers: {humanCount} human, {aiCount} AI");

                    _lastSessionInfo = currentSessionInfo;
                }
            }
        }

        /// <summary>
        /// Safely get a field value from TelemetryData using reflection
        /// Returns defaultValue if field doesn't exist or can't be accessed
        /// </summary>
        private T SafeGetFieldValue<T>(TelemetryData data, string fieldName, T defaultValue)
        {
            try
            {
                // Try to get field using reflection
                var field = data.GetType().GetField(fieldName);
                if (field != null)
                {
                    var value = field.GetValue(data);
                    if (value is T tValue)
                        return tValue;
                }

                // Try to get property using reflection
                var property = data.GetType().GetProperty(fieldName);
                if (property != null)
                {
                    var value = property.GetValue(data);
                    if (value is T tValue)
                        return tValue;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataBuilder] Error accessing field '{fieldName}': {ex.Message}");
            }

            return defaultValue;
        }

        /// <summary>
        /// Check if a field exists in the TelemetryData structure
        /// </summary>
        private bool HasField(TelemetryData data, string fieldName)
        {
            var field = data.GetType().GetField(fieldName);
            var property = data.GetType().GetProperty(fieldName);
            return field != null || property != null;
        }

        /// <summary>
        /// Validate that we have minimum required data for a valid snapshot
        /// </summary>
        public bool ValidateSnapshot(Dictionary<string, object> data)
        {
            // Check for critical fields
            if (!data.ContainsKey("PlayerCarIdx"))
                return false;

            var playerIdx = data["PlayerCarIdx"];
            if (playerIdx == null || (int)playerIdx == -1)
                return false;

            // Check for basic positioning data
            if (!data.ContainsKey("CarIdxLapDistPct"))
                return false;

            // Could add more validation here
            return true;
        }

        /// <summary>
        /// Get debug info about available telemetry fields
        /// </summary>
        public string GetAvailableFieldsDebugInfo(TelemetryData data)
        {
            var fields = new List<string>();
            var type = data.GetType();

            // Get all public fields
            foreach (var field in type.GetFields())
            {
                fields.Add($"Field: {field.Name} ({field.FieldType.Name})");
            }

            // Get all public properties
            foreach (var property in type.GetProperties())
            {
                fields.Add($"Property: {property.Name} ({property.PropertyType.Name})");
            }

            return string.Join("\n", fields);
        }
    }
}