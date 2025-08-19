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
        public int PlayerCarIdx => GetValue<int>("PlayerCarIdx", -1);
        public float SessionTime => GetValue<float>("SessionTime", 0f);
        public double SessionTimeRemain => GetValue<double>("SessionTimeRemain", 0.0);

        public SVappsLABSnapshot(Dictionary<string, object> telemetry, string sessionData, DateTime timestamp)
        {
            Timestamp = timestamp;
            IsValid = telemetry != null;
            RawTelemetryData = telemetry ?? new();
            RawSessionData = sessionData ?? string.Empty;
            SessionTick = GetValue<int>("SessionTick", 0);
        }

        /// <summary>
        /// A robust method to get a value from the telemetry data, handling type conversions safely.
        /// </summary>
        public T GetValue<T>(string fieldName, T defaultValue = default)
        {
            if (RawTelemetryData == null || !RawTelemetryData.TryGetValue(fieldName, out var value))
                return defaultValue;

            // If the type is already correct, return it directly.
            if (value is T tVal) return tVal;

            // Explicitly handle common numeric conversions to prevent silent failures.
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                // If any conversion fails, return the default value for that type.
                return defaultValue;
            }
        }
    }
}
