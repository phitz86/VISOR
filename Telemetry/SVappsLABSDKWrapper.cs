using SVappsLAB.iRacingTelemetrySDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using VISOR.Diagnostics;
using VISOR.ViewModels;
using YamlDotNet.Core;

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
        TelemetryVar.CarLeftRight, TelemetryVar.CarIdxF2Time, TelemetryVar.CarIdxEstTime, TelemetryVar.CarIdxLapCompleted,
        TelemetryVar.TrackTempCrew,
        // Shift indicator + shift-point learner (ShiftPointProvider / ShiftPointLearner)
        TelemetryVar.PlayerCarSLFirstRPM, TelemetryVar.PlayerCarSLShiftRPM, TelemetryVar.Throttle,
        TelemetryVar.Brake, TelemetryVar.Clutch, TelemetryVar.LongAccel, TelemetryVar.LatAccel, TelemetryVar.EngineWarnings,
        TelemetryVar.IsOnTrack, TelemetryVar.IsReplayPlaying, TelemetryVar.TrackWetness,
#if DEBUG
        // DEBUG-only wet-grip research (WetResearchLogger). Some may only exist in IBT files,
        // not the live feed; unavailable vars read as null and log as blank.
        TelemetryVar.Precipitation, TelemetryVar.WeatherDeclaredWet, TelemetryVar.PlayerTireCompound,
        TelemetryVar.YawRate, TelemetryVar.SteeringWheelAngle,
        TelemetryVar.LFspeed, TelemetryVar.RFspeed, TelemetryVar.LRspeed, TelemetryVar.RRspeed,
        TelemetryVar.Lat, TelemetryVar.Lon,
#endif
    ])]
    public class SVappsLABSDKWrapper : IDisposable
    {
        #region Private Fields
        private ITelemetryClient<TelemetryData> _client = null!;
        private readonly VisorSdkLogger<SVappsLABSDKWrapper> _logger;
        private readonly SessionDataCoordinator _sessionCoordinator;

        // Session info arrives on two SDK threads: the SDK's parsed result, and the raw YAML that
        // SessionInfoFallback reads itself when the SDK can't. The lock makes checking which one
        // is in charge and applying the result one step.
        private readonly SessionInfoFallback _sessionInfoFallback = new();
        private readonly object _sessionApplyLock = new();
#if DEBUG
        private readonly SessionYamlFailureLogger _sessionYamlFailures = new();
#endif
        private CancellationTokenSource _cancellationTokenSource = null!;
        private Task _monitoringTask = null!;
        private bool _isConnected = false;

        // Set when Shutdown starts. The UI thread then waits for the SDK to stop, so frames
        // raised meanwhile could only pile up behind it; they are no longer raised.
        private volatile bool _isShuttingDown;

        private int _lastSessionNumForLog = -1;
        private bool _lastPrimedState = false;
        private DateTime? _disconnectedAt = null;

        // Defensive detector state: frame-gap detector, handler latency timer, and the
        // [StreamFault] logging in RunAsync.
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
        public bool IsSessionDataReady => _sessionCoordinator.IsDataReady;
        public bool IsConnected => _isConnected;
        public bool IsPrimed => _isConnected && _sessionCoordinator.IsDataReady;
        public SessionDataCoordinator Coordinator => _sessionCoordinator;
        #endregion

        #region Events
        public event Action<SVappsLABSnapshot>? SnapshotAvailable;
        public event Action<bool>? ConnectionStateChanged;
        public event Action<bool>? PrimedStateChanged;
        #endregion

        public SVappsLABSDKWrapper()
        {
            _logger = new VisorSdkLogger<SVappsLABSDKWrapper>();
            _sessionCoordinator = new SessionDataCoordinator();

            _frameGapFlushTimer = new System.Timers.Timer(5000) { AutoReset = true };
            _frameGapFlushTimer.Elapsed += OnFrameGapFlushTimer;
        }

        public bool Initialize()
        {
            try
            {
                Log.Info("SVappsLAB SDK initialization started");

                _cancellationTokenSource = new CancellationTokenSource();
                _client = TelemetryClient<TelemetryData>.Create(_logger);
                _frameGapFlushTimer.Start();
                _monitoringTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));

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
                    // Under SDK 2.x an exception escaping any of these handlers faults Monitor and
                    // stops all streams for good (Monitor runs once per client). Every handler body
                    // below must therefore catch its own exceptions. OnError only receives
                    // SDK-side processing errors, never handler exceptions.
                    var handlers = new TelemetryHandlers<TelemetryData>
                    {
                        OnTelemetryUpdate = data => { OnTelemetryUpdate(data); return Task.CompletedTask; },
                        OnSessionInfoUpdate = info => { OnSessionInfoUpdate(info); return Task.CompletedTask; },
                        OnConnectStateChanged = state => { OnConnectStateChanged(state); return Task.CompletedTask; },
                        OnError = ex => { OnSdkError(ex); return Task.CompletedTask; },
                        OnRawSessionInfoUpdate = yaml => { OnRawSessionInfoUpdate(yaml); return Task.CompletedTask; },
                    };

                    // Defensive detector #3: stream fault logger.
                    // Monitor returns normally on cancellation; returning while shutdown was not
                    // requested, or throwing, means telemetry has stopped unexpectedly.
                    int records = await _client.Monitor(handlers, ct);
                    if (ct.IsCancellationRequested)
                    {
                        Log.Info($"SDK monitoring stopped ({records} telemetry records processed)");
                    }
                    else
                    {
                        Log.Warning($"[StreamFault] SDK Monitor ended without shutdown being requested ({records} telemetry records processed)");
                    }
                }
            }
            catch (TimeoutException ex)
            {
                // SDK 2.x throws this when a handler has not returned within 5s of monitoring ending.
                Log.Error("[StreamFault] an SDK handler did not return within 5s of shutdown", ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* expected on shutdown */ }
            catch (Exception ex)
            {
                Log.Error("[StreamFault] SDK Monitor faulted", ex);
            }
        }

        private void OnConnectStateChanged(ConnectState state)
        {
            try
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

                // Guarded separately so a throwing subscriber can't skip the cache reset below.
                try
                {
                    ConnectionStateChanged?.Invoke(_isConnected);
                }
                catch (Exception ex)
                {
                    Log.Error("ConnectionStateChanged subscriber error", ex);
                }

                if (!_isConnected)
                {
                    lock (_sessionApplyLock)
                    {
                        _sessionInfoFallback.Reset();
                        _logger.QuietSessionInfoErrors = false;
                    }
                    _sessionCoordinator.ClearCache();
                    _lastSessionNumForLog = -1;
                    // Reset frame-gap baseline so the wall-clock gap across a disconnect
                    // doesn't get reported as a single huge gap on the first frame after reconnect.
                    _lastTickTs = 0;
                }
                CheckPrimedStateChange();
            }
            catch (Exception ex)
            {
                Log.Error("OnConnectStateChanged error", ex);
            }
        }

        private void CheckPrimedStateChange()
        {
            bool isPrimed = _isConnected && _sessionCoordinator.IsDataReady;
            if (isPrimed == _lastPrimedState) return;

            Log.Info(isPrimed
                ? "HUD ready: iRacing connected and session data parsed"
                : "HUD no longer primed: waiting for connection or session data");
            _lastPrimedState = isPrimed;

            try
            {
                PrimedStateChanged?.Invoke(isPrimed);
            }
            catch (Exception ex)
            {
                Log.Error("PrimedStateChanged subscriber error", ex);
            }
        }

        private void OnSessionInfoUpdate(TelemetrySessionInfo info)
        {
            try
            {
                ApplySessionInfo(info, fromSdk: true);
            }
            catch (Exception ex)
            {
                Log.Error("OnSessionInfoUpdate error", ex);
            }
        }

        private void OnRawSessionInfoUpdate(string yaml)
        {
            try
            {
                bool wellFormed = SessionInfoYaml.IsWellFormed(yaml);
#if DEBUG
                // Debug builds keep a copy of session info that won't parse as-is.
                if (!wellFormed)
                    _sessionYamlFailures.Check(yaml);
#endif
                var info = _sessionInfoFallback.Read(yaml, wellFormed);
                if (info != null)
                    ApplySessionInfo(info, fromSdk: false);
            }
            catch (Exception ex)
            {
                Log.Error("OnRawSessionInfoUpdate error", ex);
            }
        }

        // Once VISOR reads session info itself, the SDK's results are ignored until the next
        // disconnect. Deciding that and applying happen under one lock, so an SDK result that was
        // already on its way can't land after a newer one VISOR applied.
        private void ApplySessionInfo(TelemetrySessionInfo info, bool fromSdk)
        {
            lock (_sessionApplyLock)
            {
                if (fromSdk && _sessionInfoFallback.IsActive)
                    return;
                if (!fromSdk)
                {
                    _sessionInfoFallback.Activate();
                    _logger.QuietSessionInfoErrors = true;
                }

                if (_sessionCoordinator.ApplySdkSession(info))
                {
                    CheckPrimedStateChange();
                    CheckForSessionTransitionLog();
                }
            }
        }

        private void OnSdkError(Exception ex)
        {
            // While VISOR reads session info itself, the SDK's failure to parse it repeats on
            // every update; the first one was logged in full.
            if (_sessionInfoFallback.IsActive && (ex is YamlException || ex.InnerException is YamlException))
                Log.Debug($"[SDK Stream] session info parse error (VISOR is reading it itself): {ex.Message}");
            else
                Log.Error("[SDK Stream] error from SDK", ex);
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
                _lastSessionNumForLog = currentSessionNum;
            }
        }

        private void OnTelemetryUpdate(TelemetryData telemetryData)
        {
            // Outer guard: nothing may escape into the SDK (see RunAsync).
            try
            {
                ProcessTelemetryUpdate(telemetryData);
            }
            catch (Exception ex)
            {
                Log.Error("OnTelemetryUpdate error", ex);
            }
        }

        private void ProcessTelemetryUpdate(TelemetryData telemetryData)
        {
            if (_isShuttingDown) return;

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

            // Defensive detector #2: handler latency timer. Times the inline body below: snapshot
            // construction (a thin typed wrapper) and the subscribers queuing the frame for the UI.
            // A warning here means something heavy crept back onto the SDK stream thread.
            var handlerStart = Stopwatch.GetTimestamp();

            SVappsLABSnapshot? snapshot = null;
            try
            {
                snapshot = new SVappsLABSnapshot(telemetryData);
            }
            catch (Exception ex)
            {
                Log.Error("Telemetry update error", ex);
            }

            // Raised right here, one frame at a time, so subscribers receive frames in the order the
            // SDK delivers them. (Each frame used to go out on its own Task.Run, and a later frame's
            // task could reach the UI first.) Subscribers must only queue work - the windows post
            // the frame to their UI thread - because the SDK's telemetry channel is a 60-sample ring
            // buffer that silently drops the oldest samples when consumption is slow. Each
            // subscriber is guarded on its own so one failing window can't starve the other.
            var subscribers = SnapshotAvailable;
            if (snapshot != null && subscribers != null)
            {
                foreach (Action<SVappsLABSnapshot> subscriber in subscribers.GetInvocationList())
                {
                    try
                    {
                        subscriber(snapshot);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[SnapshotFanout] snapshot subscriber error", ex);
                    }
                }
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - handlerStart) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs > HandlerLatencyWarnMs)
            {
                var cooldownTicks = (long)(HandlerLatencyCooldownSec * Stopwatch.Frequency);
                if (handlerStart - _lastLatencyLogTs > cooldownTicks)
                {
                    _lastLatencyLogTs = handlerStart;
                    Log.Warning($"[HandlerLatency] OnTelemetryUpdate took {elapsedMs:F1}ms (>{HandlerLatencyWarnMs}ms); risk of dropped samples in SDK ring buffer");
                }
            }
        }

        private void OnFrameGapFlushTimer(object? sender, System.Timers.ElapsedEventArgs e)
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
                _isShuttingDown = true;
                _frameGapFlushTimer?.Stop();
                _frameGapFlushTimer?.Dispose();

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
}
