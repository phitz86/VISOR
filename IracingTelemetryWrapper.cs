namespace VISOR.Interfaces
{
    public interface IracingTelemetryWrapper : IDisposable
    {
        // Wrapper Identity
        string Name { get; }

        // Connection State
        bool IsConnected { get; }
        bool Initialize();
        void Shutdown();

        // Snapshot Access
        TelemetrySnapshot GetSnapshot();

        // Performance Metrics
        TimeSpan LastPollDuration { get; }
        long MemoryUsageBytes { get; }
        double CpuUsagePercent { get; }

        // New field discovery methods
        HashSet<string> GetSupportedFields();
        Dictionary<string, Type> GetFieldTypes();
        bool SupportsField(string fieldName);
    }

    public class TelemetrySnapshot
    {
        public DateTime Timestamp { get; init; }
        public bool IsValid { get; init; }
        public int SessionTick { get; init; }

        public Dictionary<string, object> RawTelemetryData { get; init; } = new();
        public string RawSessionData { get; init; } = string.Empty;

        public string SessionState => GetValue("SessionState", string.Empty);
        public int PlayerCarIdx => GetValue("PlayerCarIdx", -1);
        public float SessionTime => GetValue("SessionTime", 0f);
        public double SessionTimeRemain => GetValue("SessionTimeRemain", 0.0);

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
    }
}