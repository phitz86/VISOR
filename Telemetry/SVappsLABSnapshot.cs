using System;
using System.Collections.Generic;

namespace VISOR.Telemetry
{
    public class SVappsLABSnapshot
    {
        public DateTime Timestamp { get; init; }
        public bool IsValid { get; init; }

        public Dictionary<string, object> RawTelemetryData { get; init; } = new();

        public string SessionState => GetValue("SessionState", string.Empty);
        public int PlayerCarIdx => GetValue<int>("PlayerCarIdx", -1);
        public float SessionTime => GetValue<float>("SessionTime", 0f);
        public double SessionTimeRemain => GetValue<double>("SessionTimeRemain", 0.0);

        public SVappsLABSnapshot(Dictionary<string, object> telemetry, DateTime timestamp)
        {
            Timestamp = timestamp;
            IsValid = telemetry != null;
            RawTelemetryData = telemetry ?? new();
        }

        /// <summary>
        /// A robust method to get a value from the telemetry data, handling type conversions safely.
        /// </summary>
        public T GetValue<T>(string fieldName, T defaultValue = default)
        {
            if (RawTelemetryData == null || !RawTelemetryData.TryGetValue(fieldName, out var value))
                return defaultValue;

            if (value is T tVal) return tVal;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
