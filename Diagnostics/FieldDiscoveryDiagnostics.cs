using System;
using System.Collections.Generic;
using System.Linq;
using GRiD.Interfaces;

namespace GRiD.Diagnostics
{
    public static class FieldDiscoveryDiagnostic
    {
        public static void AnalyzeFieldDiscovery(List<IracingTelemetryWrapper> wrappers)
        {
            Console.WriteLine("\n=== FIELD DISCOVERY DIAGNOSTIC ===");
            
            var allFieldsFound = new HashSet<string>();
            var wrapperFieldData = new Dictionary<string, WrapperFieldInfo>();
            
            foreach (var wrapper in wrappers)
            {
                var info = AnalyzeWrapper(wrapper);
                wrapperFieldData[wrapper.Name] = info;
                allFieldsFound.UnionWith(info.SupportedFields);
                
                Console.WriteLine($"\n--- {wrapper.Name} Analysis ---");
                Console.WriteLine($"Discovery Status: {(info.DiscoveryWorked ? "SUCCESS" : "FAILED")}");
                Console.WriteLine($"Fields Discovered: {info.SupportedFields.Count}");
                Console.WriteLine($"Current Snapshot Fields: {info.SnapshotFieldCount}");
                Console.WriteLine($"Priority Fields Available: {info.AvailablePriorityFields.Count}/24");
                
                if (info.SnapshotFieldCount > 0 && info.SupportedFields.Count == 0)
                {
                    Console.WriteLine("⚠️  WARNING: Snapshot has data but discovery found no fields!");
                    Console.WriteLine("   This suggests field discovery logic needs debugging.");
                }
                
                if (info.MissingPriorityFields.Any())
                {
                    Console.WriteLine($"Missing Priority Fields: {string.Join(", ", info.MissingPriorityFields.Take(5))}");
                    if (info.MissingPriorityFields.Count > 5)
                        Console.WriteLine($"   ... and {info.MissingPriorityFields.Count - 5} more");
                }
            }
            
            // Cross-SDK field availability matrix
            Console.WriteLine("\n=== FIELD AVAILABILITY MATRIX ===");
            Console.WriteLine("Field Name".PadRight(25) + string.Join("", wrappers.Select(w => w.Name.Substring(0, Math.Min(w.Name.Length, 12)).PadRight(13))));
            Console.WriteLine(new string('-', 25 + (wrappers.Count * 13)));
            
            var priorityFields = GetPriorityFields();
            foreach (var field in priorityFields.OrderBy(f => f))
            {
                var line = field.PadRight(25);
                foreach (var wrapper in wrappers)
                {
                    var hasField = wrapperFieldData[wrapper.Name].SupportedFields.Contains(field);
                    line += (hasField ? "✓" : "✗").PadRight(13);
                }
                Console.WriteLine(line);
            }
            
            // Field type consistency check
            Console.WriteLine("\n=== FIELD TYPE CONSISTENCY ===");
            foreach (var field in priorityFields.Where(f => allFieldsFound.Contains(f)))
            {
                var types = wrappers
                    .Where(w => wrapperFieldData[w.Name].FieldTypes.ContainsKey(field))
                    .Select(w => new { Wrapper = w.Name, Type = wrapperFieldData[w.Name].FieldTypes[field].Name })
                    .ToList();
                
                if (types.Select(t => t.Type).Distinct().Count() > 1)
                {
                    Console.WriteLine($"⚠️  TYPE MISMATCH for {field}:");
                    foreach (var typeInfo in types)
                        Console.WriteLine($"   {typeInfo.Wrapper}: {typeInfo.Type}");
                }
            }
        }
        
        private static WrapperFieldInfo AnalyzeWrapper(IracingTelemetryWrapper wrapper)
        {
            var info = new WrapperFieldInfo
            {
                SupportedFields = wrapper.GetSupportedFields(),
                FieldTypes = wrapper.GetFieldTypes()
            };
            
            info.DiscoveryWorked = info.SupportedFields.Count > 0;
            
            // Get current snapshot to see actual field count
            var snapshot = wrapper.GetSnapshot();
            info.SnapshotFieldCount = snapshot.RawTelemetryData?.Count ?? 0;
            
            var priorityFields = GetPriorityFields();
            info.AvailablePriorityFields = priorityFields.Where(f => info.SupportedFields.Contains(f)).ToList();
            info.MissingPriorityFields = priorityFields.Where(f => !info.SupportedFields.Contains(f)).ToList();
            
            return info;
        }
        
        private static string[] GetPriorityFields()
        {
            return new[]
            {
                "LapCurrentLapTime", "LapLastLapTime", "LapBestLapTime", "LapDeltaToBestLap",
                "LapDeltaToOptimalLap", "LapDeltaToSessionBestLap", "Lap",
                "FuelLevel", "FuelUsePerHour", "Gear", "Speed", "RPM",
                "CarIdxLapDistPct", "CarIdxPosition", "CarIdxClassPosition", "CarIdxTrackSurface",
                "DriverCarIdx", "CarIdxLap", "CarIdxLastLapTime",
                "SessionState", "SessionTime", "SessionTimeRemain", "SessionLapsRemain",
                "SessionLapsTotal", "SessionNum", "SessionType",
                "TrackTemp", "AirTemp", "Skies", "WindVel"
            };
        }
        
        private class WrapperFieldInfo
        {
            public HashSet<string> SupportedFields { get; set; } = new();
            public Dictionary<string, Type> FieldTypes { get; set; } = new();
            public bool DiscoveryWorked { get; set; }
            public int SnapshotFieldCount { get; set; }
            public List<string> AvailablePriorityFields { get; set; } = new();
            public List<string> MissingPriorityFields { get; set; } = new();
        }
    }
}