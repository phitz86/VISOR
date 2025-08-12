using System;
using System.Collections.Generic;

namespace VISOR.Telemetry
{
    public class SVappsLABSnapshot
    {
        public DateTime Timestamp { get; init; }
        public bool IsValid { get; init; }
        public int SessionTick { get; init; }

        public Dictionary<string, object> RawTelemetryData { get; init; } = new();
        public string RawSessionData { get; init; } = string.Empty;

        // Convenience properties for common fields
        public string SessionState => GetValue("SessionState", string.Empty);
        public int PlayerCarIdx => GetValue("PlayerCarIdx", -1);
        public float SessionTime => GetValue("SessionTime", 0f);
        public double SessionTimeRemain => GetValue("SessionTimeRemain", 0.0);
        public int SessionNum => GetValue("SessionNum", 0);
        public string SessionType => GetValue("SessionType", "Unknown");
        public int Gear => GetValue("Gear", 0);
        public float Speed => GetValue("Speed", 0f);

        public SVappsLABSnapshot(Dictionary<string, object> telemetry, string sessionData, DateTime timestamp)
        {
            Timestamp = timestamp;
            IsValid = telemetry != null;
            RawTelemetryData = telemetry ?? new();
            RawSessionData = sessionData ?? string.Empty;
            SessionTick = GetValue("SessionTick", 0);

            // This ensures values are present and correctly typed
            telemetry?.TryAdd("SessionTime", GetSafeFloat(telemetry, "SessionTime"));
            telemetry?.TryAdd("SessionTimeRemain", GetSafeDouble(telemetry, "SessionTimeRemain"));
            telemetry?.TryAdd("PlayerCarIdx", GetSafeInt(telemetry, "PlayerCarIdx"));
            telemetry?.TryAdd("SessionState", GetSafeString(telemetry, "SessionState"));
        }

        public T GetValue<T>(string fieldName, T defaultValue = default)
        {
            if (RawTelemetryData == null || !RawTelemetryData.TryGetValue(fieldName, out var value))
                return defaultValue;

            if (value is T tVal) return tVal;

            try
            {
                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                return (T)Convert.ChangeType(value, targetType);
            }
            catch
            {
                return defaultValue;
            }
        }

        public bool HasField(string fieldName)
        {
            return RawTelemetryData?.ContainsKey(fieldName) == true;
        }

        public bool IsFieldStale(string fieldName, TimeSpan maxAge)
        {
            // Optional: implement actual field-level timestamping later
            return false;
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