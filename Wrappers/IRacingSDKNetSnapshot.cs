using System;
using System.Collections.Generic;
using GRiD.Interfaces;

namespace GRiD.Wrappers
{
    public class IRacingSDKNetSnapshot : TelemetrySnapshot
    {
        public IRacingSDKNetSnapshot(Dictionary<string, object> telemetry, string sessionData, DateTime timestamp)
        {
            Timestamp = timestamp;
            IsValid = telemetry != null;
            RawTelemetryData = telemetry ?? new();
            RawSessionData = sessionData ?? string.Empty;
            // This ensures values are present and correctly typed
            telemetry.TryAdd("SessionTime", GetSafeFloat(telemetry, "SessionTime"));
            telemetry.TryAdd("SessionTimeRemain", GetSafeDouble(telemetry, "SessionTimeRemain"));
            telemetry.TryAdd("PlayerCarIdx", GetSafeInt(telemetry, "PlayerCarIdx"));
            telemetry.TryAdd("SessionState", GetSafeString(telemetry, "SessionState"));
        }

        private static int GetSafeInt(Dictionary<string, object> dict, string key)
        {
            return dict != null && dict.TryGetValue(key, out var val) && val is int i ? i : -1;
        }
        private static float GetSafeFloat(Dictionary<string, object> dict, string key)
        {
            return dict != null && dict.TryGetValue(key, out var val) && val is float f ? f : 0f;
        }
        private static double GetSafeDouble(Dictionary<string, object> dict, string key)
        {
            return dict != null && dict.TryGetValue(key, out var val) && val is double d ? d : 0.0;
        }
        private static string GetSafeString(Dictionary<string, object> dict, string key)
        {
            return dict != null && dict.TryGetValue(key, out var val) && val is string s ? s : string.Empty;
        }

    }
}
