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
        private readonly SVappsLABSDKWrapper _wrapper;

        // Static fields for tracking state changes
        private static int _lastSessionState = -1;
        private static int _lastSessionFlags = -1;
        private static string _lastSessionInfo = "";

        public TelemetryDataBuilder(SVappsLABSDKWrapper wrapper)
        {
            _wrapper = wrapper ?? throw new ArgumentNullException(nameof(wrapper));
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

                // Merge in YAML-parsed data from wrapper's session parser
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

            // CarIdxOnPitRoad might not be available in current TelemetryData struct
            // Will be available when we update the RequiredTelemetryVars attribute
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

            // SessionFlags might not be available in current TelemetryData struct
            // Will need to add to RequiredTelemetryVars to get it

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

        private void AddYamlData(Dictionary<string, object> dict)
        {
            // Add YAML-parsed arrays from session parser
            dict["CarIdxUserName"] = _wrapper.GetUserNames();
            dict["CarIdxCarNumber"] = _wrapper.GetCarNumbers();
            dict["CarIdxCarNumberRaw"] = _wrapper.GetCarNumberRaw();
            dict["CarIdxClassID"] = _wrapper.GetCarClassIDs();
            dict["CarIdxIsAI"] = _wrapper.GetCarIsAI();
            dict["CarIdxIncidentCount"] = _wrapper.GetCurDriverIncidentCount();

            // NEW: Add session-specific YAML data
            dict["SessionType"] = _wrapper.GetSessionType();
            dict["SessionName"] = _wrapper.GetSessionName();
            dict["QualifyResultsPositions"] = _wrapper.GetQualifyResultsPositions();
            dict["QualifyResultsFastestTimes"] = _wrapper.GetQualifyResultsFastestTimes();

            // DEBUG: Log session info when it's available
            var sessionType = _wrapper.GetSessionType();
            var sessionName = _wrapper.GetSessionName();
            if (!string.IsNullOrEmpty(sessionType))
            {
                string currentSessionInfo = $"{sessionType}({sessionName})";
                if (currentSessionInfo != _lastSessionInfo)
                {
                    Console.WriteLine($"[DataBuilder DEBUG] Session Info: {currentSessionInfo}");

                    // Count human vs AI drivers for debug
                    var carIsAI = _wrapper.GetCarIsAI();
                    int humanCount = 0, aiCount = 0;
                    foreach (var isAI in carIsAI)
                    {
                        if (isAI) aiCount++; else humanCount++;
                    }
                    Console.WriteLine($"[DataBuilder DEBUG] Driver counts - Human: {humanCount}, AI: {aiCount}");

                    _lastSessionInfo = currentSessionInfo;
                }
            }
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

            // Could add more validation here
            return true;
        }
    }
}