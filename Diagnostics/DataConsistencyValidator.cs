using System;
using System.Collections.Generic;
using System.Linq;
using GRiD.Interfaces;

namespace GRiD.Diagnostics
{
    public static class DataConsistencyValidator
    {
        public static void ValidateConsistency(List<IracingTelemetryWrapper> wrappers)
        {
            Console.WriteLine("\n=== DATA CONSISTENCY VALIDATION ===");

            // Take snapshots from all connected wrappers
            var snapshots = wrappers
                .Where(w => w.IsConnected)
                .Select(w => new WrapperSnapshot { Wrapper = w.Name, Snapshot = w.GetSnapshot() })
                .Where(s => s.Snapshot.IsValid)
                .ToList();

            if (snapshots.Count < 2)
            {
                Console.WriteLine("⚠️  Need at least 2 connected wrappers for consistency validation");
                return;
            }

            Console.WriteLine($"Validating consistency across {snapshots.Count} wrappers:");
            foreach (var s in snapshots)
                Console.WriteLine($"  - {s.Wrapper}: {s.Snapshot.RawTelemetryData.Count} fields");

            // Find common fields across all snapshots
            var commonFields = snapshots
                .Select(s => s.Snapshot.RawTelemetryData.Keys.ToHashSet())
                .Aggregate((a, b) => { a.IntersectWith(b); return a; });

            Console.WriteLine($"\nCommon fields across all wrappers: {commonFields.Count}");

            // Critical fields for overlay functionality
            var criticalFields = new[]
            {
                "SessionTime", "SessionTimeRemain", "SessionState", "Speed", "RPM",
                "Gear", "FuelLevel", "PlayerCarIdx", "SessionTick"
            };

            Console.WriteLine("\n=== CRITICAL FIELD ANALYSIS ===");
            foreach (var field in criticalFields)
            {
                AnalyzeFieldConsistency(field, snapshots);
            }

            // Look for timing discrepancies
            Console.WriteLine("\n=== TIMING ANALYSIS ===");
            AnalyzeTimingConsistency(snapshots);

            // Session state analysis
            Console.WriteLine("\n=== SESSION STATE ANALYSIS ===");
            AnalyzeSessionStateConsistency(snapshots);
        }

        private static void AnalyzeFieldConsistency(string fieldName,
            List<WrapperSnapshot> snapshots)
        {
            var values = new List<(string wrapper, object value, Type type)>();

            foreach (var snapshot in snapshots)
            {
                if (snapshot.Snapshot.RawTelemetryData.ContainsKey(fieldName))
                {
                    var value = snapshot.Snapshot.RawTelemetryData[fieldName];
                    values.Add((snapshot.Wrapper, value, value?.GetType()));
                }
            }

            if (!values.Any())
            {
                Console.WriteLine($"{fieldName}: NOT AVAILABLE in any wrapper");
                return;
            }

            // Check if all wrappers have this field
            if (values.Count != snapshots.Count)
            {
                var missing = snapshots.Where(s => !s.Snapshot.RawTelemetryData.ContainsKey(fieldName))
                                      .Select(s => s.Wrapper);
                Console.WriteLine($"{fieldName}: MISSING from {string.Join(", ", missing)}");
            }

            // Check type consistency
            var types = values.Select(v => v.type?.Name ?? "null").Distinct().ToList();
            if (types.Count > 1)
            {
                Console.WriteLine($"{fieldName}: TYPE INCONSISTENCY");
                foreach (var value in values)
                    Console.WriteLine($"  {value.wrapper}: {value.type?.Name ?? "null"}");
            }

            // Check value consistency (for numeric fields)
            if (values.All(v => IsNumeric(v.value)))
            {
                var numericValues = values.Select(v => Convert.ToDouble(v.value)).ToList();
                var min = numericValues.Min();
                var max = numericValues.Max();
                var range = max - min;

                if (range > Math.Abs(max) * 0.01) // >1% difference
                {
                    Console.WriteLine($"{fieldName}: VALUE INCONSISTENCY (range: {range:F3})");
                    foreach (var value in values)
                        Console.WriteLine($"  {value.wrapper}: {value.value}");
                }
                else if (values.Count > 1)
                {
                    Console.WriteLine($"{fieldName}: ✓ Consistent ({numericValues[0]:F3})");
                }
            }
            else if (values.Count > 1)
            {
                // Check string/object consistency
                var distinctValues = values.Select(v => v.value?.ToString()).Distinct().ToList();
                if (distinctValues.Count > 1)
                {
                    Console.WriteLine($"{fieldName}: VALUE INCONSISTENCY");
                    foreach (var value in values)
                        Console.WriteLine($"  {value.wrapper}: {value.value}");
                }
                else
                {
                    Console.WriteLine($"{fieldName}: ✓ Consistent ({distinctValues[0]})");
                }
            }
        }

        private static void AnalyzeTimingConsistency(List<WrapperSnapshot> snapshots)
        {
            // Check if session times are consistent
            var sessionTimes = snapshots
                .Where(s => s.Snapshot.RawTelemetryData.ContainsKey("SessionTime"))
                .Select(s => new {
                    Wrapper = s.Wrapper,
                    Time = Convert.ToDouble(s.Snapshot.RawTelemetryData["SessionTime"]),
                    Timestamp = s.Snapshot.Timestamp
                })
                .ToList();

            if (sessionTimes.Count > 1)
            {
                var timeDiffs = sessionTimes.Select(st => st.Time).ToList();
                var maxDiff = timeDiffs.Max() - timeDiffs.Min();

                if (maxDiff > 1.0) // More than 1 second difference
                {
                    Console.WriteLine("⚠️  SIGNIFICANT SessionTime discrepancy:");
                    foreach (var st in sessionTimes)
                        Console.WriteLine($"  {st.Wrapper}: {st.Time:F2}s");
                }
                else
                {
                    Console.WriteLine($"✓ SessionTime consistent (±{maxDiff:F3}s)");
                }
            }

            // Check snapshot timestamp consistency
            var timestamps = snapshots.Select(s => s.Snapshot.Timestamp).ToList();
            if (timestamps.Count > 1)
            {
                var maxTimeDiff = timestamps.Max() - timestamps.Min();
                if (maxTimeDiff.TotalMilliseconds > 100)
                {
                    Console.WriteLine($"⚠️  Snapshot timing spread: {maxTimeDiff.TotalMilliseconds:F1}ms");
                    Console.WriteLine("   Consider synchronized polling for better consistency");
                }
                else
                {
                    Console.WriteLine($"✓ Snapshot timing tight ({maxTimeDiff.TotalMilliseconds:F1}ms spread)");
                }
            }
        }

        private static void AnalyzeSessionStateConsistency(List<WrapperSnapshot> snapshots)
        {
            var sessionStates = snapshots
                .Select(s => new {
                    Wrapper = s.Wrapper,
                    State = s.Snapshot.RawTelemetryData.ContainsKey("SessionState")
                        ? s.Snapshot.RawTelemetryData["SessionState"]?.ToString()
                        : "MISSING",
                    StateProperty = s.Snapshot.SessionState
                })
                .ToList();

            Console.WriteLine("Session State Comparison:");
            foreach (var state in sessionStates)
            {
                Console.WriteLine($"  {state.Wrapper}: Raw={state.State}, Property={state.StateProperty}");
            }

            // Check for state mapping inconsistencies
            var rawStates = sessionStates.Select(s => s.State).Distinct().ToList();
            var propertyStates = sessionStates.Select(s => s.StateProperty).Distinct().ToList();

            if (rawStates.Count > 1)
                Console.WriteLine("⚠️  Raw SessionState values differ between wrappers");

            if (propertyStates.Count > 1)
                Console.WriteLine("⚠️  SessionState property mappings differ between wrappers");
        }

        private class WrapperSnapshot
        {
            public string Wrapper { get; set; }
            public TelemetrySnapshot Snapshot { get; set; }
        }

        private static bool IsNumeric(object value)
        {
            return value != null && (
                value is int || value is float || value is double ||
                value is decimal || value is long || value is short
            );
        }
    }
}