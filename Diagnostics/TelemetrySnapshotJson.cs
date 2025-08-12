using System;
using System.Collections.Generic;

namespace VISOR.Diagnostics
{
    public class TelemetrySnapshotJson
    {
        public string Timestamp { get; set; }
        public string Wrapper { get; set; }
        public int SessionTick { get; set; }
        public float SessionTime { get; set; }
        public double SessionTimeRemain { get; set; }
        public int PlayerCarIdx { get; set; }
        public string SessionState { get; set; }

        public PerformanceMetrics Performance { get; set; }
        public Dictionary<string, object> Fields { get; set; }

        public class PerformanceMetrics
        {
            public double DurationMs { get; set; }
            public long MemoryBytes { get; set; }
            public double CpuPercent { get; set; }
        }
    }
}