using GRiD.Diagnostics;
using GRiD.Interfaces;
using GRiD.Logging;
using GRiD.Services;
using GRiD.Wrappers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GRiD.Services
{
    public class TelemetryInspectorService : IDisposable
    {
        private readonly List<IracingTelemetryWrapper> _wrappers;
        private readonly TelemetrySnapshotLogger _logger;
        private readonly WrapperPerformanceMonitor _performanceMonitor;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan? _maxDuration;
        private volatile bool _running;
        private CancellationTokenSource _cancellationTokenSource;

        public TelemetryInspectorService(List<IracingTelemetryWrapper> wrappers, TelemetrySnapshotLogger logger, TimeSpan? maxDuration = null, TimeSpan? interval = null)
        {
            _wrappers = wrappers ?? throw new ArgumentNullException(nameof(wrappers));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pollInterval = interval ?? TimeSpan.FromMilliseconds(16); // ~60 FPS default
            _maxDuration = maxDuration;
            _performanceMonitor = new WrapperPerformanceMonitor(@"C:\Users\cepha\Desktop\GRiD\Logs");
            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task Start()
        {
            _running = true;
            Console.WriteLine("[GRiD] Starting telemetry inspector loop...");

            // Initialize wrappers with enhanced error handling
            var initializedWrappers = await InitializeWrappers();

            if (initializedWrappers.Count == 0)
            {
                Console.WriteLine("[ERROR] No wrappers successfully initialized. Exiting.");
                return;
            }

            Console.WriteLine($"[GRiD] Successfully initialized {initializedWrappers.Count}/{_wrappers.Count} wrappers");
            Console.WriteLine("[GRiD] Starting performance monitoring...");

            // Wait for connections to stabilize
            await Task.Delay(2000, _cancellationTokenSource.Token);

            await RunPollingLoop(initializedWrappers);
        }

        private async Task<List<IracingTelemetryWrapper>> InitializeWrappers()
        {
            var initializedWrappers = new List<IracingTelemetryWrapper>();

            foreach (var wrapper in _wrappers)
            {
                Console.WriteLine($"[GRiD] Initializing wrapper: {wrapper.Name}");

                try
                {
                    bool success = await InitializeWrapperSafely(wrapper);

                    if (success)
                    {
                        initializedWrappers.Add(wrapper);
                        Console.WriteLine($"[GRiD] ✓ Successfully initialized: {wrapper.Name}");

                        // Give each wrapper time to settle
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                    }
                    else
                    {
                        Console.WriteLine($"[ERROR] ✗ Failed to initialize wrapper: {wrapper.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Exception initializing {wrapper.Name}: {ex.Message}");
                    _logger?.LogError(wrapper.Name, "Initialization failed", ex);
                }
            }

            return initializedWrappers;
        }

        private async Task<bool> InitializeWrapperSafely(IracingTelemetryWrapper wrapper)
        {
            try
            {
                // Handle async initialization for SVappsLAB
                if (wrapper is SVappsLABSDKWrapper svappsWrapper)
                {
                    var initMethod = svappsWrapper.GetType().GetMethod("Initialize");
                    if (initMethod != null)
                    {
                        var result = initMethod.Invoke(svappsWrapper, null);
                        if (result is Task<bool> taskResult)
                        {
                            return await taskResult;
                        }
                        else if (result is bool boolResult)
                        {
                            return boolResult;
                        }
                    }
                }

                // Standard synchronous initialization
                return wrapper.Initialize();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] InitializeWrapperSafely({wrapper.Name}): {ex.Message}");
                return false;
            }
        }

        private async Task RunPollingLoop(List<IracingTelemetryWrapper> initializedWrappers)
        {
            var startTime = DateTime.UtcNow;
            var nextPoll = DateTime.UtcNow;
            int pollCount = 0;
            int connectedWarningCounter = 0;
            if (pollCount == 300) // ~5 seconds into runtime
            {
                Console.WriteLine("[GRiD] Running delayed diagnostics after initial data capture...");
                FieldDiscoveryDiagnostic.AnalyzeFieldDiscovery(initializedWrappers);
                DataConsistencyValidator.ValidateConsistency(initializedWrappers);
            }

            Console.WriteLine("[GRiD] Entering main polling loop...");

            try
            {
                while (_running && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    nextPoll = nextPoll.Add(_pollInterval);
                    pollCount++;

                    // Check for duration limit
                    if (_maxDuration.HasValue && DateTime.UtcNow - startTime > _maxDuration.Value)
                    {
                        Console.WriteLine("[GRiD] Max duration reached. Stopping...");
                        Stop();
                        break;
                    }

                    var timestamp = DateTime.UtcNow;
                    bool anyConnected = false;
                    int connectedCount = 0;

                    // Poll all wrappers
                    foreach (var wrapper in initializedWrappers)
                    {
                        try
                        {
                            bool isConnected = wrapper.IsConnected;
                            if (isConnected)
                            {
                                anyConnected = true;
                                connectedCount++;
                            }

                            // Get snapshot regardless of connection status for performance monitoring
                            var snapshot = wrapper.GetSnapshot();
                            
                            // Log snapshot
                            _logger.LogSnapshot(wrapper.Name, timestamp, snapshot, wrapper.LastPollDuration, wrapper.MemoryUsageBytes, wrapper.CpuUsagePercent);
                            
                            // Record performance metrics
                            _performanceMonitor.RecordSample(wrapper.Name, isConnected, snapshot, wrapper.LastPollDuration, wrapper.MemoryUsageBytes, wrapper.CpuUsagePercent);

                            // Periodic connection status logging
                            if (!isConnected && pollCount % 300 == 0) // Every ~5 seconds at 60fps
                            {
                                Console.WriteLine($"[WARN] {wrapper.Name} not connected.");
                            }
                            else if (isConnected && pollCount % 600 == 0) // Every ~10 seconds
                            {
                                Console.WriteLine($"[INFO] {wrapper.Name}: SessionTick={snapshot.SessionTick}, State={snapshot.SessionState}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ERROR] Exception polling {wrapper.Name}: {ex.Message}");
                            _logger?.LogError(wrapper.Name, "Polling error", ex);
                        }
                    }

                    // Global connection status warnings
                    if (!anyConnected)
                    {
                        connectedWarningCounter++;
                        if (connectedWarningCounter % 300 == 0) // Every ~5 seconds
                        {
                            Console.WriteLine($"[WARN] No wrappers connected to iRacing (check {connectedWarningCounter / 60}s)");
                        }
                    }
                    else
                    {
                        connectedWarningCounter = 0; // Reset counter when we have connections
                        
                        // Periodic status update when connected
                        if (pollCount % 1800 == 0) // Every ~30 seconds
                        {
                            Console.WriteLine($"[INFO] Active connections: {connectedCount}/{initializedWrappers.Count} wrappers");
                        }
                    }

                    // Wait for next poll interval
                    var delay = nextPoll - DateTime.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _cancellationTokenSource.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[GRiD] Polling loop cancelled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Polling loop error: {ex.Message}");
                _logger?.LogError("Inspector", "Polling loop error", ex);
            }
            finally
            {
                Console.WriteLine("[GRiD] Polling loop ended.");
            }
        }

        public void Stop()
        {
            if (!_running) return;

            _running = false;
            Console.WriteLine("[GRiD] Stopping telemetry inspector...");

            try
            {
                // Cancel the polling loop
                _cancellationTokenSource?.Cancel();

                // Generate final performance report
                Console.WriteLine("[GRiD] Generating final performance report...");
                _performanceMonitor?.GenerateFinalReport();

                // Shutdown all wrappers
                foreach (var wrapper in _wrappers)
                {
                    try
                    {
                        Console.WriteLine($"[GRiD] Shutting down {wrapper.Name}...");
                        wrapper.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Error shutting down {wrapper.Name}: {ex.Message}");
                        _logger?.LogError(wrapper.Name, "Shutdown error", ex);
                    }
                }

                Console.WriteLine("[GRiD] ✓ Inspector service stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error during stop: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
            
            try
            {
                _cancellationTokenSource?.Dispose();
                _performanceMonitor?.Dispose();
                
                foreach (var wrapper in _wrappers)
                {
                    wrapper?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Error during dispose: {ex.Message}");
            }
        }
    }
}