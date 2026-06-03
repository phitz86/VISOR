using Microsoft.Extensions.Logging;
using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VISOR.Diagnostics;
using VISOR.ViewModels;

namespace VISOR.Telemetry
{
    [RequiredTelemetryVars([
        TelemetryVar.LapCurrentLapTime, TelemetryVar.LapLastLapTime, TelemetryVar.LapBestLapTime, TelemetryVar.LapDeltaToBestLap,
        TelemetryVar.LapDeltaToOptimalLap, TelemetryVar.LapDeltaToSessionBestLap, TelemetryVar.Lap,
        TelemetryVar.FuelLevel, TelemetryVar.FuelUsePerHour, TelemetryVar.Gear, TelemetryVar.Speed, TelemetryVar.RPM,
        TelemetryVar.CarIdxLapDistPct, TelemetryVar.CarIdxPosition, TelemetryVar.CarIdxClassPosition, TelemetryVar.CarIdxTrackSurface,
        TelemetryVar.CarIdxLap, TelemetryVar.CarIdxLastLapTime, TelemetryVar.CarIdxBestLapTime, TelemetryVar.CarIdxOnPitRoad,
        TelemetryVar.SessionState, TelemetryVar.SessionTime, TelemetryVar.SessionTimeRemain, TelemetryVar.SessionLapsRemain,
        TelemetryVar.SessionLapsTotal, TelemetryVar.SessionNum, TelemetryVar.PlayerCarIdx, TelemetryVar.SessionFlags,
        TelemetryVar.CarLeftRight, TelemetryVar.CarIdxF2Time, TelemetryVar.CarIdxEstTime, TelemetryVar.CarIdxLapCompleted
    ])]
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields
        private ITelemetryClient<TelemetryData> _client;
        private readonly ILogger _logger;
        private readonly SessionDataCoordinator _sessionCoordinator;
#if DEBUG
        private readonly SessionDataLogger _sessionLogger;
        private readonly TelemetryCSVLogger _telemetryLogger;
#endif
        private SVappsLABSnapshot _latestSnapshot;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _monitoringTask;
        private bool _isConnected = false;

        // Cached raw YAML from onRawSessionInfoUpdate; consumed only by the
        // DEBUG SessionDataLogger. Reference assignment is atomic in C#, so
        // the lock-free read pattern matches the volatile driver-cache arrays.
        private volatile string _cachedRawYaml = string.Empty;

        private int _lastSessionNumForLog = -1;
        private bool _lastPrimedState = false;
        private DateTime? _disconnectedAt = null;

        // Defensive detector state (see Planning/SDK-Migration-Roadmap.md §"Defensive logging strategy")
        private long _lastTickTs;
        private int _frameGapCount;
        private double _worstGapMs;
        private readonly object _frameGapLock = new();
        private readonly System.Timers.Timer _frameGapFlushTimer;
        private long _lastLatencyLogTs;
        private const double FrameGapThresholdMs = 33.0;     // ~2 missed 60Hz frames
        private const double HandlerLatencyWarnMs = 10.0;    // margin before 16.6ms danger zone
        private const double HandlerLatencyCooldownSec = 5.0;
        #endregion

        #region Public Properties
        public string Name => "SVappsLAB iRacingTelemetrySDK";
        public bool IsSessionDataReady => _sessionCoordinator.IsDataReady;
        public bool IsConnected => _isConnected;
        public bool IsPrimed => _isConnected && _sessionCoordinator.IsDataReady;
        public SessionDataCoordinator Coordinator => _sessionCoordinator;
        #endregion

        #region Events
        public event Action<SVappsLABSnapshot> SnapshotAvailable;
        public event Action<bool> ConnectionStateChanged;
        public event Action<bool> PrimedStateChanged;
        #endregion

        public SVappsLABSDKWrapper()
        {
            _logger = new VisorSdkLogger<SVappsLABSDKWrapper>();
            _sessionCoordinator = new SessionDataCoordinator();

#if DEBUG
            _sessionLogger = new SessionDataLogger(() => _cachedRawYaml);
            _telemetryLogger = new TelemetryCSVLogger();
#endif

            _frameGapFlushTimer = new System.Timers.Timer(5000) { AutoReset = true };
            _frameGapFlushTimer.Elapsed += OnFrameGapFlushTimer;
        }

        public async Task<bool> Initialize()
        {
            try
            {
                Log.Info("SVappsLAB SDK initialization started");

                _cancellationTokenSource = new CancellationTokenSource();
                _client = TelemetryClient<TelemetryData>.Create(_logger);
                _frameGapFlushTimer.Start();
                _monitoringTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));

                await Task.Delay(200);
                Log.Info("SVappsLAB SDK initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("SVappsLAB SDK initialization error", ex);
                return false;
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            try
            {
                await using (_client)
                {
                    var subscriptionTask = _client.SubscribeToAllStreams(
                        onTelemetryUpdate: data => { OnTelemetryUpdate(data); return Task.CompletedTask; },
                        onRawSessionInfoUpdate: yaml => { OnRawSessionInfoUpdate(yaml); return Task.CompletedTask; },
                        onSessionInfoUpdate: info => { OnSessionInfoUpdate(info); return Task.CompletedTask; },
                        onConnectStateChanged: state => { OnConnectStateChanged(state); return Task.CompletedTask; },
                        onError: ex => { Log.Error("[SDK Stream] error from SDK", ex); return Task.CompletedTask; },
                        cancellationToken: ct);
                    var monitorTask = _client.Monitor(ct);

                    // Defensive detector #3: stream task fault logger.
                    // Task.WhenAny returns the first task to finish; if it finished unexpectedly
                    // (faulted, or completed without shutdown being requested), surface it.
                    var winner = await Task.WhenAny(monitorTask, subscriptionTask);
                    if (winner.IsFaulted)
                    {
                        Log.Error("[StreamFault] SDK task ended unexpectedly", winner.Exception);
                    }
                    else if (!ct.IsCancellationRequested)
                    {
                        Log.Warning("[StreamFault] SDK task ended without exception (expected only on shutdown)");
                    }

                    // Observe any pending exception on the other task so it doesn't disappear into the void.
                    try
                    {
                        await Task.WhenAll(monitorTask, subscriptionTask);
                    }
                    catch (OperationCanceledException) { /* expected on shutdown */ }
                    catch (Exception ex)
                    {
                        Log.Error("[StreamFault] additional exception while draining SDK tasks", ex);
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex)
            {
                Log.Error("[SDK Stream] RunAsync error", ex);
            }
        }

        public SVappsLABSnapshot GetSnapshot() => _latestSnapshot;

        private void OnConnectStateChanged(ConnectState state)
        {
            bool newConnectionState = state == ConnectState.Connected;
            if (newConnectionState == _isConnected) return;

            _isConnected = newConnectionState;

            if (_isConnected && _disconnectedAt.HasValue)
            {
                var duration = DateTime.UtcNow - _disconnectedAt.Value;
                Log.Info($"iRacing reconnected after {duration.TotalSeconds:F1}s disconnection");
                _disconnectedAt = null;
            }
            else
            {
                Log.Info($"iRacing connection state changed: {(_isConnected ? "Connected" : "Disconnected")}");
                if (!_isConnected)
                    _disconnectedAt = DateTime.UtcNow;
            }

            ConnectionStateChanged?.Invoke(_isConnected);

            if (!_isConnected)
            {
                _sessionCoordinator.ClearCache();
                _cachedRawYaml = string.Empty;
                _lastSessionNumForLog = -1;
                // Reset frame-gap baseline so the wall-clock gap across a disconnect
                // doesn't get reported as a single huge gap on the first frame after reconnect.
                _lastTickTs = 0;
            }
            CheckPrimedStateChange();
        }

        private void CheckPrimedStateChange()
        {
            bool isPrimed = _isConnected && _sessionCoordinator.IsDataReady;
            if (isPrimed != _lastPrimedState)
            {
                Log.Info(isPrimed
                    ? "HUD ready: iRacing connected and session data parsed"
                    : "HUD no longer primed: waiting for connection or session data");
                _lastPrimedState = isPrimed;
            }
            PrimedStateChanged?.Invoke(isPrimed);
        }

        private void OnRawSessionInfoUpdate(string sessionInfo)
        {
            // Stash for the DEBUG-only SessionDataLogger; parsing happens in OnSessionInfoUpdate.
            _cachedRawYaml = sessionInfo ?? string.Empty;
        }

        private void OnSessionInfoUpdate(TelemetrySessionInfo info)
        {
            try
            {
                if (_sessionCoordinator.ApplySdkSession(info))
                {
                    CheckPrimedStateChange();
                    CheckForSessionTransitionLog();
                }
            }
            catch (Exception ex)
            {
                Log.Error("OnSessionInfoUpdate error", ex);
            }
        }

        private void CheckForSessionTransitionLog()
        {
            int currentSessionNum = _sessionCoordinator.CurrentSessionNum;
            if (currentSessionNum >= 0 && currentSessionNum != _lastSessionNumForLog)
            {
                string sessionType = _sessionCoordinator.GetSessionType(currentSessionNum);
                string sessionName = _sessionCoordinator.GetSessionName(currentSessionNum);
                double sessionTimeSeconds = _sessionCoordinator.GetSessionTimeSeconds(currentSessionNum);

                Log.Info($"Session transition: {sessionName} (Type: {sessionType}, Duration: {sessionTimeSeconds}s)");

#if DEBUG
                _sessionLogger?.ScheduleSessionAwareLogs(currentSessionNum, sessionName, sessionTimeSeconds);
#endif
                _lastSessionNumForLog = currentSessionNum;
            }
        }

        private void OnTelemetryUpdate(TelemetryData telemetryData)
        {
            // Defensive detector #1: frame-gap detector. 60Hz expected, flag gaps >33ms.
            var now = Stopwatch.GetTimestamp();
            if (_lastTickTs != 0)
            {
                var gapMs = (now - _lastTickTs) * 1000.0 / Stopwatch.Frequency;
                if (gapMs > FrameGapThresholdMs)
                {
                    lock (_frameGapLock)
                    {
                        _frameGapCount++;
                        if (gapMs > _worstGapMs) _worstGapMs = gapMs;
                    }
                }
            }
            _lastTickTs = now;

            // Defensive detector #2: handler latency timer. Times the inline body below.
            // Post-offload the inline cost is just snapshot construction (a thin typed wrapper);
            // a warning here means something heavy crept back onto the SDK stream thread.
            var handlerStart = Stopwatch.GetTimestamp();

            SVappsLABSnapshot snapshot = null;
            try
            {
                snapshot = new SVappsLABSnapshot(telemetryData, DateTime.UtcNow);
                _latestSnapshot = snapshot;
            }
            catch (Exception ex)
            {
                Log.Error("Telemetry update error", ex);
            }

            // Offload the fan-out and DEBUG-only file I/O so the SDK stream thread isn't
            // blocked on consumer work. In SDK 1.x the channel has a 60-sample ring buffer
            // and oldest samples are silently dropped when consumption is slow; keeping
            // the on-thread cost minimal is the prescribed mitigation.
            if (snapshot != null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
#if DEBUG
                        _telemetryLogger?.LogSnapshot(snapshot, _sessionCoordinator);
#endif
                        SnapshotAvailable?.Invoke(snapshot);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[SnapshotFanout] error in offloaded snapshot fan-out", ex);
                    }
                });
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - handlerStart) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs > HandlerLatencyWarnMs)
            {
                var cooldownTicks = (long)(HandlerLatencyCooldownSec * Stopwatch.Frequency);
                if (handlerStart - _lastLatencyLogTs > cooldownTicks)
                {
                    _lastLatencyLogTs = handlerStart;
                    Log.Warning($"[HandlerLatency] OnTelemetryUpdate took {elapsedMs:F1}ms (>{HandlerLatencyWarnMs}ms); risk of dropped samples in 1.x ring buffer");
                }
            }
        }

        private void OnFrameGapFlushTimer(object sender, System.Timers.ElapsedEventArgs e)
        {
            int count;
            double worst;
            lock (_frameGapLock)
            {
                if (_frameGapCount == 0) return;
                count = _frameGapCount;
                worst = _worstGapMs;
                _frameGapCount = 0;
                _worstGapMs = 0;
            }
            Log.Warning($"[FrameGap] {count} gap(s) >{FrameGapThresholdMs:F0}ms between telemetry samples in last 5s (worst: {worst:F1}ms)");
        }

        public void Shutdown()
        {
            try
            {
                Log.Info("SVappsLAB SDK shutdown initiated");
                _frameGapFlushTimer?.Stop();
                _frameGapFlushTimer?.Dispose();

#if DEBUG
                _sessionLogger?.Dispose();
                _telemetryLogger?.Dispose();
#endif

                _cancellationTokenSource?.Cancel();

                // RunAsync owns the `await using` on _client; cancellation unwinds it and disposes the SDK client.
                if (_monitoringTask != null && !_monitoringTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    Log.Warning("Run task did not shut down gracefully");
                }

                _cancellationTokenSource?.Dispose();
                _sessionCoordinator.ClearCache();
                Log.Info("SVappsLAB SDK shutdown complete");
            }
            catch (Exception ex)
            {
                Log.Error("SVappsLAB SDK shutdown error", ex);
            }
        }

        public void Dispose()
        {
            Shutdown();
        }
    }

    /// <summary>
    /// Bridges Microsoft.Extensions.Logging output from the SVappsLAB SDK into VISOR's Log.cs.
    /// Warnings and errors are always surfaced; Info/Debug are forwarded when VISOR debug mode is on.
    /// </summary>
    public class VisorSdkLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning || Diagnostics.Log.DebugModeEnabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (formatter == null) return;

            string message = $"[SDK] {formatter(state, exception)}";

            switch (logLevel)
            {
                case LogLevel.Critical:
                case LogLevel.Error:
                    Diagnostics.Log.Error(message, exception);
                    break;
                case LogLevel.Warning:
                    Diagnostics.Log.Warning(exception == null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})");
                    break;
                case LogLevel.Information:
                    Diagnostics.Log.Info(message);
                    break;
                default:
                    Diagnostics.Log.Debug(message);
                    break;
            }
        }
    }
}