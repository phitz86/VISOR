using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using GRiD.Interfaces;

namespace GRiD.Services
{
    public class WrapperPerformanceMonitor : IDisposable
    {
        private readonly Dictionary<string, WrapperStats> _wrapperStats = new();
        private readonly string _outputDirectory;
        private readonly Stopwatch _sessionTimer = Stopwatch.StartNew();
        private DateTime _lastReportTime = DateTime.UtcNow;
        private readonly TimeSpan _reportInterval = TimeSpan.FromSeconds(30);
        private bool _disposed = false;

        public string OutputDirectory => _outputDirectory;

        public WrapperPerformanceMonitor(string outputDirectory)
        {
            _outputDirectory = outputDirectory;
            Directory.CreateDirectory(_outputDirectory);
        }

        public void RecordSample(string wrapperName, bool isConnected, TelemetrySnapshot snapshot,
            TimeSpan pollDuration, long memoryBytes, double cpuPercent)
        {
            if (_disposed) return;

            if (!_wrapperStats.ContainsKey(wrapperName))
            {
                _wrapperStats[wrapperName] = new WrapperStats(wrapperName);
            }

            var stats = _wrapperStats[wrapperName];
            stats.RecordSample(isConnected, snapshot, pollDuration, memoryBytes, cpuPercent);

            // Generate periodic reports
            if (DateTime.UtcNow - _lastReportTime >= _reportInterval)
            {
                GeneratePerformanceReport();
                _lastReportTime = DateTime.UtcNow;
            }
        }

        public void GeneratePerformanceReport()
        {
            if (_disposed) return;

            try
            {
                var report = new PerformanceReport
                {
                    Timestamp = DateTime.UtcNow,
                    SessionDuration = _sessionTimer.Elapsed,
                    Wrappers = _wrapperStats.Values.Select(s => s.GetSummary()).ToList()
                };

                // Console output
                Console.WriteLine("\n" + "=".PadRight(80, '='));
                Console.WriteLine($"PERFORMANCE REPORT - {report.Timestamp:HH:mm:ss} (Session: {report.SessionDuration:mm\\:ss})");
                Console.WriteLine("=".PadRight(80, '='));

                foreach (var wrapper in report.Wrappers.OrderByDescending(w => w.ConnectionSuccessRate))
                {
                    Console.WriteLine($"\n{wrapper.Name}:");
                    Console.WriteLine($"  Connection: {wrapper.ConnectionSuccessRate:P1} ({wrapper.ConnectedSamples}/{wrapper.TotalSamples})");
                    Console.WriteLine($"  Data Quality: {wrapper.ValidDataRate:P1} ({wrapper.ValidDataSamples}/{wrapper.TotalSamples})");
                    Console.WriteLine($"  Avg Poll Time: {wrapper.AveragePollDuration:F1}ms (Min: {wrapper.MinPollDuration:F1}ms, Max: {wrapper.MaxPollDuration:F1}ms)");
                    Console.WriteLine($"  Memory Usage: {wrapper.AverageMemoryMB:F1}MB");
                    Console.WriteLine($"  CPU Usage: {wrapper.AverageCpuPercent:F1}%");
                    Console.WriteLine($"  Fields Retrieved: {wrapper.AverageFieldCount:F0} fields/sample");

                    if (wrapper.ErrorCount > 0)
                    {
                        Console.WriteLine($"  Errors: {wrapper.ErrorCount} ({wrapper.ErrorRate:P1})");
                    }
                }

                // JSON output
                var jsonPath = Path.Combine(_outputDirectory, "performance-report.jsonl");
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.AppendAllText(jsonPath, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Performance report generation failed: {ex.Message}");
            }
        }

        public void GenerateFinalReport()
        {
            if (_disposed) return;

            try
            {
                Console.WriteLine("\n" + "=".PadRight(80, '='));
                Console.WriteLine("FINAL PERFORMANCE SUMMARY");
                Console.WriteLine("=".PadRight(80, '='));

                var finalReport = new
                {
                    SessionDuration = _sessionTimer.Elapsed,
                    TotalSamples = _wrapperStats.Values.Sum(s => s.TotalSamples),
                    Wrappers = _wrapperStats.Values.Select(s => s.GetSummary()).OrderByDescending(s => s.ConnectionSuccessRate).ToList(),
                    Recommendations = GenerateRecommendations()
                };

                foreach (var wrapper in finalReport.Wrappers)
                {
                    Console.WriteLine($"\n{wrapper.Name} - FINAL STATS:");
                    Console.WriteLine($"  Overall Score: {CalculateOverallScore(wrapper):F1}/100");
                    Console.WriteLine($"  Reliability: {wrapper.ConnectionSuccessRate:P1}");
                    Console.WriteLine($"  Performance: {wrapper.AveragePollDuration:F1}ms avg");
                    Console.WriteLine($"  Resource Usage: {wrapper.AverageMemoryMB:F1}MB, {wrapper.AverageCpuPercent:F1}% CPU");
                    Console.WriteLine($"  Data Completeness: {wrapper.AverageFieldCount:F0} fields");
                }

                Console.WriteLine($"\nRECOMMENDATIONS:");
                foreach (var rec in finalReport.Recommendations)
                {
                    Console.WriteLine($"  • {rec}");
                }

                // Save final report
                var finalPath = Path.Combine(_outputDirectory, $"final-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                var finalJson = JsonSerializer.Serialize(finalReport, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(finalPath, finalJson);

                Console.WriteLine($"\nFinal report saved to: {finalPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Final report generation failed: {ex.Message}");
            }
        }

        private double CalculateOverallScore(WrapperSummary wrapper)
        {
            // Weighted scoring: Reliability (40%), Performance (30%), Data Quality (20%), Resource Usage (10%)
            var reliabilityScore = wrapper.ConnectionSuccessRate * 40;
            var performanceScore = Math.Max(0, (50 - wrapper.AveragePollDuration) / 50 * 30); // 50ms = 0 score, 0ms = 30 score
            var dataQualityScore = (wrapper.ValidDataRate * 0.7 + Math.Min(1.0, wrapper.AverageFieldCount / 20) * 0.3) * 20;
            var resourceScore = Math.Max(0, (200 - wrapper.AverageMemoryMB) / 200 * 0.5 + (10 - wrapper.AverageCpuPercent) / 10 * 0.5) * 10;

            return reliabilityScore + performanceScore + dataQualityScore + resourceScore;
        }

        private List<string> GenerateRecommendations()
        {
            var recommendations = new List<string>();
            var summaries = _wrapperStats.Values.Select(s => s.GetSummary()).ToList();

            if (!summaries.Any()) return recommendations;

            // Find best performers
            var bestReliability = summaries.OrderByDescending(s => s.ConnectionSuccessRate).First();
            var bestPerformance = summaries.OrderBy(s => s.AveragePollDuration).First();
            var bestResourceUsage = summaries.OrderBy(s => s.AverageMemoryMB + s.AverageCpuPercent).First();

            recommendations.Add($"Most Reliable: {bestReliability.Name} ({bestReliability.ConnectionSuccessRate:P1} connection success)");
            recommendations.Add($"Fastest Polling: {bestPerformance.Name} ({bestPerformance.AveragePollDuration:F1}ms average)");
            recommendations.Add($"Most Efficient: {bestResourceUsage.Name} (lowest resource usage)");

            // Overall recommendation
            var overallBest = summaries.OrderByDescending(CalculateOverallScore).First();
            recommendations.Add($"Overall Recommendation: {overallBest.Name} (score: {CalculateOverallScore(overallBest):F1}/100)");

            // Specific advice
            var unreliableWrappers = summaries.Where(s => s.ConnectionSuccessRate < 0.8).ToList();
            if (unreliableWrappers.Any())
            {
                recommendations.Add($"Connection Issues: {string.Join(", ", unreliableWrappers.Select(w => w.Name))} - check iRacing setup and SDK versions");
            }

            var slowWrappers = summaries.Where(s => s.AveragePollDuration > 10).ToList();
            if (slowWrappers.Any())
            {
                recommendations.Add($"Performance Concerns: {string.Join(", ", slowWrappers.Select(w => w.Name))} - consider reducing poll frequency");
            }

            return recommendations;
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                _sessionTimer?.Stop();
                GenerateFinalReport();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error during performance monitor disposal: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    public class WrapperStats
    {
        public string Name { get; }
        public int TotalSamples { get; private set; }
        public int ConnectedSamples { get; private set; }
        public int ValidDataSamples { get; private set; }
        public int ErrorCount { get; private set; }

        private readonly List<double> _pollDurations = new();
        private readonly List<long> _memoryUsages = new();
        private readonly List<double> _cpuUsages = new();
        private readonly List<int> _fieldCounts = new();

        public WrapperStats(string name)
        {
            Name = name;
        }

        public void RecordSample(bool isConnected, TelemetrySnapshot snapshot, TimeSpan pollDuration, long memoryBytes, double cpuPercent)
        {
            TotalSamples++;

            if (isConnected)
            {
                ConnectedSamples++;
            }

            if (snapshot?.IsValid == true && snapshot.RawTelemetryData?.Count > 0)
            {
                ValidDataSamples++;
                _fieldCounts.Add(snapshot.RawTelemetryData.Count);
            }
            else if (isConnected)
            {
                ErrorCount++;
            }

            _pollDurations.Add(pollDuration.TotalMilliseconds);
            _memoryUsages.Add(memoryBytes);
            _cpuUsages.Add(cpuPercent);
        }

        public WrapperSummary GetSummary()
        {
            return new WrapperSummary
            {
                Name = Name,
                TotalSamples = TotalSamples,
                ConnectedSamples = ConnectedSamples,
                ValidDataSamples = ValidDataSamples,
                ErrorCount = ErrorCount,
                ConnectionSuccessRate = TotalSamples > 0 ? (double)ConnectedSamples / TotalSamples : 0,
                ValidDataRate = TotalSamples > 0 ? (double)ValidDataSamples / TotalSamples : 0,
                ErrorRate = TotalSamples > 0 ? (double)ErrorCount / TotalSamples : 0,
                AveragePollDuration = _pollDurations.Count > 0 ? _pollDurations.Average() : 0,
                MinPollDuration = _pollDurations.Count > 0 ? _pollDurations.Min() : 0,
                MaxPollDuration = _pollDurations.Count > 0 ? _pollDurations.Max() : 0,
                AverageMemoryMB = _memoryUsages.Count > 0 ? _memoryUsages.Average() / (1024 * 1024) : 0,
                AverageCpuPercent = _cpuUsages.Count > 0 ? _cpuUsages.Average() : 0,
                AverageFieldCount = _fieldCounts.Count > 0 ? _fieldCounts.Average() : 0
            };
        }
    }

    public class WrapperSummary
    {
        public string Name { get; set; }
        public int TotalSamples { get; set; }
        public int ConnectedSamples { get; set; }
        public int ValidDataSamples { get; set; }
        public int ErrorCount { get; set; }
        public double ConnectionSuccessRate { get; set; }
        public double ValidDataRate { get; set; }
        public double ErrorRate { get; set; }
        public double AveragePollDuration { get; set; }
        public double MinPollDuration { get; set; }
        public double MaxPollDuration { get; set; }
        public double AverageMemoryMB { get; set; }
        public double AverageCpuPercent { get; set; }
        public double AverageFieldCount { get; set; }
    }

    public class PerformanceReport
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan SessionDuration { get; set; }
        public List<WrapperSummary> Wrappers { get; set; } = new();
    }
}