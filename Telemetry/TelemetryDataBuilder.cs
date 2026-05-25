using System;
using System.Collections.Generic;
using SVappsLAB.iRacingTelemetrySDK;
using VISOR.Diagnostics;

namespace VISOR.Telemetry
{
    public class TelemetryDataBuilder
    {
        private readonly SessionDataCoordinator _coordinator;
        private string _lastSessionInfo = "";

        // Defensive detector #4: first-time-null logger. Tracks which declared telemetry
        // variables have ever been observed null so we log each at most once per process.
        private readonly HashSet<string> _observedNullFields = new();
        private readonly object _firstNullLock = new();

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
                Log.Error($"[DataBuilder] Error building telemetry dictionary: {ex.Message}");
            }
            return dict;
        }

        private void AddLapTimingData(Dictionary<string, object> dict, TelemetryData data)
        {
            Set(dict, "LapCurrentLapTime", data.LapCurrentLapTime);
            Set(dict, "LapLastLapTime", data.LapLastLapTime);
            Set(dict, "LapBestLapTime", data.LapBestLapTime);
            Set(dict, "LapDeltaToBestLap", data.LapDeltaToBestLap);
            Set(dict, "LapDeltaToOptimalLap", data.LapDeltaToOptimalLap);
            Set(dict, "LapDeltaToSessionBestLap", data.LapDeltaToSessionBestLap);
            Set(dict, "Lap", data.Lap);
        }

        private void AddCarStateData(Dictionary<string, object> dict, TelemetryData data)
        {
            Set(dict, "FuelLevel", data.FuelLevel);
            Set(dict, "FuelUsePerHour", data.FuelUsePerHour);
            Set(dict, "Gear", data.Gear);
            Set(dict, "Speed", data.Speed);
            Set(dict, "RPM", data.RPM);
        }

        private void AddPositioningData(Dictionary<string, object> dict, TelemetryData data)
        {
            SetArray(dict, "CarIdxLapDistPct", data.CarIdxLapDistPct);
            SetArray(dict, "CarIdxPosition", data.CarIdxPosition);
            SetArray(dict, "CarIdxClassPosition", data.CarIdxClassPosition);
            SetArray(dict, "CarIdxTrackSurface", data.CarIdxTrackSurface);
            SetArray(dict, "CarIdxLap", data.CarIdxLap);
            SetArray(dict, "CarIdxLapCompleted", data.CarIdxLapCompleted);
            SetArray(dict, "CarIdxLastLapTime", data.CarIdxLastLapTime);
            SetArray(dict, "CarIdxBestLapTime", data.CarIdxBestLapTime);
            SetArray(dict, "CarIdxOnPitRoad", data.CarIdxOnPitRoad);
            SetArray(dict, "CarIdxEstTime", data.CarIdxEstTime);
        }

        private void AddSessionData(Dictionary<string, object> dict, TelemetryData data)
        {
            // SessionState compiled as non-nullable enum under SDK 1.2.1; keep the direct cast.
            dict["SessionState"] = (int)data.SessionState;
            Set(dict, "SessionTime", data.SessionTime);
            Set(dict, "SessionTimeRemain", data.SessionTimeRemain);
            Set(dict, "SessionLapsRemain", data.SessionLapsRemain);
            Set(dict, "SessionLapsTotal", data.SessionLapsTotal);
            Set(dict, "SessionNum", data.SessionNum);
            dict["SessionFlags"] = Convert.ToInt32(data.SessionFlags);
        }

        private void AddPlayerData(Dictionary<string, object> dict, TelemetryData data)
        {
            // -1 sentinel matches SVappsLABSnapshot.PlayerCarIdx default for "no player".
            Set(dict, "PlayerCarIdx", data.PlayerCarIdx, defaultValue: -1);
        }

        private void AddRadarData(Dictionary<string, object> dict, TelemetryData data)
        {
            // CarLeftRight is an enum on TelemetryData; box into the dict and let
            // downstream consumers cast. Detect nulls in case it becomes nullable.
            if (data.CarLeftRight is null) NotifyFirstNull("CarLeftRight");
            dict["CarLeftRight"] = data.CarLeftRight;

            SetArray(dict, "CarIdxF2Time", data.CarIdxF2Time);
            // Note: TrackLength is sourced from session YAML in AddYamlData; the prior
            // reflection read against TelemetryData always fell back to 0f (not declared
            // in [RequiredTelemetryVars]), so dropping it is a no-op.
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
                    Log.Debug($"[DataBuilder] Session Info: {currentSessionInfo}");
                    _lastSessionInfo = currentSessionInfo;
                }
            }
        }

        private void Set(Dictionary<string, object> dict, string name, float? value, float defaultValue = 0f)
        {
            if (!value.HasValue) NotifyFirstNull(name);
            dict[name] = value ?? defaultValue;
        }

        private void Set(Dictionary<string, object> dict, string name, int? value, int defaultValue = 0)
        {
            if (!value.HasValue) NotifyFirstNull(name);
            dict[name] = value ?? defaultValue;
        }

        private void Set(Dictionary<string, object> dict, string name, double? value, double defaultValue = 0.0)
        {
            if (!value.HasValue) NotifyFirstNull(name);
            dict[name] = value ?? defaultValue;
        }

        private void SetArray<T>(Dictionary<string, object> dict, string name, T[] value, int expectedLength = 64)
        {
            if (value is null)
            {
                NotifyFirstNull(name);
                dict[name] = new T[expectedLength];
            }
            else
            {
                dict[name] = value;
            }
        }

        private void NotifyFirstNull(string fieldName)
        {
            bool firstObservation;
            lock (_firstNullLock)
            {
                firstObservation = _observedNullFields.Add(fieldName);
            }
            if (firstObservation)
            {
                Log.Warning($"[FirstNull] {fieldName} observed null for the first time");
            }
        }

        public bool ValidateSnapshot(Dictionary<string, object> data)
        {
            if (!data.ContainsKey("PlayerCarIdx") || (int)data["PlayerCarIdx"] == -1)
                return false;

            if (!data.ContainsKey("CarIdxLapDistPct"))
                return false;

            return true;
        }
    }
}
