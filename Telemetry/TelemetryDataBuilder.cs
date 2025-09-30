using System;
using System.Collections.Generic;
using SVappsLAB.iRacingTelemetrySDK;

namespace VISOR.Telemetry
{
    public class TelemetryDataBuilder
    {
        private readonly SessionDataCoordinator _coordinator;
        private static int _lastSessionState = -1;
        private static int _lastSessionFlags = -1;
        private static string _lastSessionInfo = "";

        public TelemetryDataBuilder(SessionDataCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public Dictionary<string, object> BuildTelemetryDictionary(TelemetryData telemetryData)
        {
            var dict = new Dictionary<string, object>();
            try
            {
                AddLapTimingData(dict, telemetryData);
                AddCarStateData(dict, telemetryData);
                AddPositioningData(dict, telemetryData);
                AddSessionData(dict, telemetryData);
                AddPlayerData(dict, telemetryData);
                AddRadarData(dict, telemetryData);
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
            dict["CarIdxLapDistPct"] = data.CarIdxLapDistPct;
            dict["CarIdxPosition"] = data.CarIdxPosition;
            dict["CarIdxClassPosition"] = data.CarIdxClassPosition;
            dict["CarIdxTrackSurface"] = data.CarIdxTrackSurface;
            dict["CarIdxLap"] = data.CarIdxLap;
            dict["CarIdxLastLapTime"] = data.CarIdxLastLapTime;
            dict["CarIdxOnPitRoad"] = SafeGetFieldValue(data, "CarIdxOnPitRoad", new bool[64]);
        }

        private void AddSessionData(Dictionary<string, object> dict, TelemetryData data)
        {
            dict["SessionState"] = (int)data.SessionState;
            dict["SessionTime"] = data.SessionTime;
            dict["SessionTimeRemain"] = data.SessionTimeRemain;
            dict["SessionLapsRemain"] = data.SessionLapsRemain;
            dict["SessionLapsTotal"] = data.SessionLapsTotal;
            dict["SessionNum"] = data.SessionNum;

            // --- UPDATED: Convert the SDK's enum to a standard integer ---
            dict["SessionFlags"] = Convert.ToInt32(SafeGetFieldValue<object>(data, "SessionFlags", 0));

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
            dict["CarLeftRight"] = SafeGetFieldValue<object>(data, "CarLeftRight", null);
            dict["CarIdxF2Time"] = SafeGetFieldValue(data, "CarIdxF2Time", new float[64]);
            dict["TrackLength"] = SafeGetFieldValue(data, "TrackLength", 0f);

            if (dict["CarIdxF2Time"] is float[] carIdxF2Time && carIdxF2Time.Length > 0 && (float)dict["TrackLength"] > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[DataBuilder] Radar data available - TrackLength: {(float)dict["TrackLength"]:F1}m");
            }
        }

        private void AddYamlData(Dictionary<string, object> dict)
        {
            dict["CarIdxUserName"] = _coordinator.UserNames;
            dict["CarIdxCarNumber"] = _coordinator.CarNumbers;
            dict["CarIdxCarNumberRaw"] = _coordinator.CarNumberRaw;
            dict["CarIdxClassID"] = _coordinator.CarClassIDs;
            dict["CarIdxIsAI"] = _coordinator.CarIsAI;
            dict["CarIdxIncidentCount"] = _coordinator.CurDriverIncidentCount;
            dict["SessionType"] = _coordinator.GetCurrentSessionType();
            dict["SessionName"] = _coordinator.GetCurrentSessionName();
            dict["QualifyResultsPositions"] = _coordinator.GetQualifyResultsPositions();
            dict["QualifyResultsFastestTimes"] = _coordinator.GetQualifyResultsFastestTimes();
            dict["TrackLength"] = _coordinator.GetTrackLength();

            var sessionType = _coordinator.GetCurrentSessionType();
            if (!string.IsNullOrEmpty(sessionType))
            {
                string currentSessionInfo = $"{sessionType}({_coordinator.GetCurrentSessionName()})";
                if (currentSessionInfo != _lastSessionInfo)
                {
                    Console.WriteLine($"[DataBuilder DEBUG] Session Info: {currentSessionInfo}");
                    System.Diagnostics.Debug.WriteLine($"[DataBuilder] Session Info: {currentSessionInfo}");

                    var trackLength = _coordinator.GetTrackLength();
                    if (trackLength > 0)
                    {
                        Console.WriteLine($"[DataBuilder DEBUG] Track Length: {trackLength:F1}m");
                        System.Diagnostics.Debug.WriteLine($"[DataBuilder] Track Length: {trackLength:F1}m");
                    }

                    var carIsAI = _coordinator.CarIsAI;
                    int humanCount = carIsAI.Count(isAI => !isAI);
                    int aiCount = carIsAI.Count(isAI => isAI);
                    Console.WriteLine($"[DataBuilder DEBUG] Driver counts - Human: {humanCount}, AI: {aiCount}");
                    System.Diagnostics.Debug.WriteLine($"[DataBuilder] Drivers: {humanCount} human, {aiCount} AI");

                    _lastSessionInfo = currentSessionInfo;
                }
            }
        }

        private T SafeGetFieldValue<T>(TelemetryData data, string fieldName, T defaultValue)
        {
            try
            {
                var field = data.GetType().GetField(fieldName);
                if (field != null)
                {
                    var value = field.GetValue(data);
                    if (value is T tValue)
                        return tValue;
                }

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

        private bool HasField(TelemetryData data, string fieldName)
        {
            var field = data.GetType().GetField(fieldName);
            var property = data.GetType().GetProperty(fieldName);
            return field != null || property != null;
        }

        public bool ValidateSnapshot(Dictionary<string, object> data)
        {
            if (!data.ContainsKey("PlayerCarIdx") || (int)data["PlayerCarIdx"] == -1)
                return false;

            if (!data.ContainsKey("CarIdxLapDistPct"))
                return false;

            return true;
        }

        public string GetAvailableFieldsDebugInfo(TelemetryData data)
        {
            var fields = new List<string>();
            var type = data.GetType();

            foreach (var field in type.GetFields())
            {
                fields.Add($"Field: {field.Name} ({field.FieldType.Name})");
            }

            foreach (var property in type.GetProperties())
            {
                fields.Add($"Property: {property.Name} ({property.PropertyType.Name})");
            }

            return string.Join("\n", fields);
        }
    }
}