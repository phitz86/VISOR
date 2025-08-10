using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VISOR.Interfaces;
using VISOR.Logging;
using VISOR.Services;
using VISOR.Wrappers;
using VISOR.Diagnostics;

class Program
{
    private static TelemetryInspectorService inspector;
    private static TelemetrySnapshotLogger logger;

    static async Task Main(string[] args)
    {
        ConsoleLogger.Initialize("logs");
        // Initialize encoding provider for IRSDKSharper
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Show banner
        ShowBanner();

        // Parse command line arguments
        var config = ParseArguments(args);

        // Run diagnostics if requested
        if (config.RunDiagnostics)
        {
            Console.WriteLine("[VISOR] Running connection diagnostics...");
            ConnectionDiagnostics.RunDiagnostics();
            if (config.DiagnosticsOnly)
            {
                return;
            }
            Console.WriteLine("\nPress any key to continue to telemetry inspection...");
            Console.ReadKey();
        }

        // Get run duration from user if not specified
        if (!config.Duration.HasValue)
        {
            config.Duration = GetDurationFromUser();
        }

        Console.WriteLine("[VISOR] Initializing telemetry inspection service with performance monitoring...");

        // Initialize logger
        logger = new TelemetrySnapshotLogger(config.OutputDirectory);

        // Initialize wrappers
        var wrappers = new List<IracingTelemetryWrapper>
        {
            new IRSDKSharperWrapper(),
            new SVappsLABSDKWrapper(),
            new IRacingSDKNetWrapper() // Using the enhanced version
        };
        // Log session start
        logger.LogSessionStart(DateTime.UtcNow, wrappers.Select(w => w.Name).ToList(), config.Duration);

        // Create inspector service with integrated performance monitoring
        inspector = new TelemetryInspectorService(wrappers, logger, config.Duration);

        // Setup graceful shutdown
        SetupGracefulShutdown();

        try
        {
            Console.WriteLine($"[VISOR] Starting inspection service with enhanced monitoring...");
            if (config.Duration.HasValue)
            {
                Console.WriteLine($"[VISOR] Run duration: {config.Duration.Value.TotalMinutes:F1} minutes");
            }
            else
            {
                Console.WriteLine("[VISOR] No duration limit. Press Ctrl+C to stop.");
            }

            Console.WriteLine("[VISOR] Features enabled:");
            Console.WriteLine("  • Real-time performance monitoring");
            Console.WriteLine("  • Enhanced connection diagnostics");
            Console.WriteLine("  • Automatic retry mechanisms");
            Console.WriteLine("  • Detailed error reporting");
            Console.WriteLine("[VISOR] Press Ctrl+C at any time to stop gracefully...");
            Console.WriteLine();

            await inspector.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Inspector service error: {ex.Message}");
            logger?.LogError("System", "Inspector service error", ex);
        }
        finally
        {
            await Cleanup();
        }
    }

    private static void ShowBanner()
    {
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine("VISOR - iRacing SDK Diagnostic Tool v2.0");
        Console.WriteLine("Enhanced with Performance Monitoring & Advanced Diagnostics");
        Console.WriteLine($"Build Date: {DateTime.Now:yyyy-MM-dd}");
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine();
    }

    private static Config ParseArguments(string[] args)
    {
        var config = new Config();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--output":
                case "-o":
                    if (i + 1 < args.Length)
                    {
                        config.OutputDirectory = args[++i];
                    }
                    break;

                case "--duration":
                case "-d":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int minutes))
                    {
                        config.Duration = TimeSpan.FromMinutes(minutes);
                    }
                    break;

                case "--diagnostics":
                case "--diag":
                    config.RunDiagnostics = true;
                    break;

                case "--diagnostics-only":
                case "--diag-only":
                    config.RunDiagnostics = true;
                    config.DiagnosticsOnly = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    ShowHelp();
                    Environment.Exit(0);
                    break;

                default:
                    // Treat as output directory if no flag specified
                    if (!args[i].StartsWith("-") && string.IsNullOrEmpty(config.OutputDirectory))
                    {
                        config.OutputDirectory = args[i];
                    }
                    break;
            }
        }

        return config;
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Usage: VISOR-C.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --output <directory>    Output directory for logs (default: logs)");
        Console.WriteLine("  -d, --duration <minutes>    Run duration in minutes (default: interactive)");
        Console.WriteLine("  --diag, --diagnostics       Run connection diagnostics before starting");
        Console.WriteLine("  --diag-only                  Run diagnostics only and exit");
        Console.WriteLine("  -h, --help                   Show this help message");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  VISOR-C.exe --duration 5 --output results");
        Console.WriteLine("  VISOR-C.exe --diagnostics");
        Console.WriteLine("  VISOR-C.exe --diag-only");
        Console.WriteLine();
        Console.WriteLine("Enhanced Features v2.0:");
        Console.WriteLine("  • Real-time performance comparison");
        Console.WriteLine("  • Automatic connection retry mechanisms");
        Console.WriteLine("  • Enhanced error handling and diagnostics");
        Console.WriteLine("  • Detailed scoring and recommendations");
        Console.WriteLine();
        Console.WriteLine("The tool compares three iRacing SDK wrappers:");
        Console.WriteLine("  • IRSDKSharper");
        Console.WriteLine("  • SVappsLAB.iRacingTelemetrySDK");
        Console.WriteLine("  • iRacingSDK.Net (Enhanced)");
        Console.WriteLine();
        Console.WriteLine("Ensure iRacing is running with telemetry enabled (irsdkEnableMem=1)");
    }

    private static TimeSpan? GetDurationFromUser()
    {
        Console.Write("Enter duration in minutes (or leave blank to run until Ctrl+C): ");
        var input = Console.ReadLine();

        if (int.TryParse(input, out int minutes) && minutes > 0)
        {
            Console.WriteLine($"[VISOR] Run duration set to {minutes} minute(s).");
            return TimeSpan.FromMinutes(minutes);
        }
        else
        {
            Console.WriteLine("[VISOR] No duration limit set. Press Ctrl+C to stop.");
            return null;
        }
    }

    private static void SetupGracefulShutdown()
    {
        bool shutdownRequested = false;

        AppDomain.CurrentDomain.ProcessExit += async (_, __) =>
        {
            if (!shutdownRequested)
            {
                shutdownRequested = true;
                Console.WriteLine("\n[VISOR] Process exit detected, shutting down gracefully...");
                await Cleanup();
            }
        };

        Console.CancelKeyPress += async (_, e) =>
        {
            e.Cancel = true;
            if (!shutdownRequested)
            {
                shutdownRequested = true;
                Console.WriteLine("\n[VISOR] Shutdown requested (Ctrl+C), stopping gracefully...");
                inspector?.Stop();
                await Cleanup();
                Environment.Exit(0);
            }
        };
    }

    private static async Task Cleanup()
    {
        Console.WriteLine("[VISOR] Performing cleanup...");

        try
        {
            // Stop inspector if still running
            inspector?.Stop();
            await Task.Delay(2000); // Give more time for cleanup

            // Dispose inspector (which will generate final reports)
            inspector?.Dispose();

            // Log session end and create reports
            if (logger != null)
            {
                var sessionDuration = inspector != null ? TimeSpan.Zero : TimeSpan.Zero; // Will be calculated by performance monitor
                logger.LogSessionEnd(DateTime.UtcNow, sessionDuration, new Dictionary<string, int>());
                logger.CreateSummaryReport();
                logger.Flush();
            }

            Console.WriteLine("[VISOR] ✓ Cleanup complete. Check output directory for detailed reports.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VISOR] Error during cleanup: {ex.Message}");
        }
    }

    private class Config
    {
        public string OutputDirectory { get; set; } = "logs";
        public TimeSpan? Duration { get; set; }
        public bool RunDiagnostics { get; set; }
        public bool DiagnosticsOnly { get; set; }
    }
}