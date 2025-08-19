using System;
using System.Collections.Generic;
using SVappsLAB.iRacingTelemetrySDK;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Builds telemetry dictionaries from raw SDK data and merges with session data.
    /// </summary>
    public class TelemetryDataBuilder
    {
        private readonly SessionDataParser _sessionParser;

        public TelemetryDataBuilder(SessionDataParser sessionParser)
        {
            _sessionParser = sessionParser ?? throw new ArgumentNullException(nameof(sessionParser));
        }

        /// <summary>
        /// Build a complete telemetry dictionary from raw telemetry data
        /// </summary>
        public Dictionary<string, object> BuildTelemetryDictionary(TelemetryData telemetryData)
        {
            var dict = new Dictionary<string, object>();

            try
            {
                // Add all telemetry fields
                AddLapTimingData(dict, telemetryData);
                AddCarStateData(dict, telemetryData);
                AddPositioningData(dict, telemetryData);
                AddSessionData(dict, telemetryData);
                AddPlayerData(dict, telemetryData);

                // Merge in YAML-parsed data
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
            // Positioning arrays - validate before adding
            if (data.CarIdxLapDistPct != null)
                dict["CarIdxLapDistPct"] = data.CarIdxLapDistPct;

            if (data.CarIdxPosition != null)
                dict["CarIdxPosition"] = data.CarIdxPosition;

            if (data.CarIdxClassPosition != null)
                dict["CarIdxClassPosition"] = data.CarIdxClassPosition;

            if (data.CarIdxTrackSurface != null)
                dict["CarIdxTrackSurface"] = data.CarIdxTrackSurface;

            if (data.CarIdxLap != null)
                dict["CarIdxLap"] = data.CarIdxLap;

            if (data.CarIdxLastLapTime != null)
                dict["CarIdxLastLapTime"] = data.CarIdxLastLapTime;
        }

        private void AddSessionData(Dictionary<string, object> dict, TelemetryData data)
        {
            dict["SessionState"] = data.SessionState;
            dict["SessionTime"] = data.SessionTime;
            dict["SessionTimeRemain"] = data.SessionTimeRemain;
            dict["SessionLapsRemain"] = data.SessionLapsRemain;
            dict["SessionLapsTotal"] = data.SessionLapsTotal;
            dict["SessionNum"] = data.SessionNum;
        }

        private void AddPlayerData(Dictionary<string, object> dict, TelemetryData data)
        {
            dict["PlayerCarIdx"] = data.PlayerCarIdx;
        }

        private void AddYamlData(Dictionary<string, object> dict)
        {
            // Add YAML-parsed arrays
            dict["CarIdxCarNumber"] = _sessionParser.CarNumbers;
            dict["CarIdxClass"] = _sessionParser.CarClasses;

            // Could add more YAML data here if needed
            // dict["CarIdxDriverName"] = _sessionParser.DriverNames;
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