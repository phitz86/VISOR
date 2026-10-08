using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using VISOR.Diagnostics;
using VISOR.Telemetry;

namespace VISOR.Views
{
    /// <summary>
    /// Hands telemetry frames from the SDK's telemetry thread to a window's UI thread, in the
    /// order they arrive, without making the SDK wait.
    ///
    /// BeginInvoke from the one thread that raises frames, all at the same priority, runs them on
    /// the UI thread strictly in that order. Send is the priority the blocking Invoke this
    /// replaces used, so frames are handled as promptly as before. If the UI thread stalls,
    /// frames beyond a short backlog are dropped instead of queued without bound, and the drops
    /// are logged.
    /// </summary>
    internal sealed class FramePoster
    {
        // ~66 ms behind at 60 Hz. Only reached if the UI thread stalls.
        private const int MaxPendingFrames = 4;
        private static readonly long DropLogIntervalTicks = 5 * Stopwatch.Frequency;

        private readonly Dispatcher _dispatcher;
        private readonly string _name;
        private readonly Action<SVappsLABSnapshot> _handler;

        private int _pendingFrames;
        private int _droppedFrames;
        private long _lastDropLogTs;

        public FramePoster(Dispatcher dispatcher, string name, Action<SVappsLABSnapshot> handler)
        {
            _dispatcher = dispatcher;
            _name = name;
            _handler = handler;
        }

        /// <summary>Queues a frame for the UI thread. Called on the SDK's telemetry thread.</summary>
        public void Post(SVappsLABSnapshot snapshot)
        {
            if (Interlocked.Increment(ref _pendingFrames) > MaxPendingFrames)
            {
                Interlocked.Decrement(ref _pendingFrames);
                NoteDroppedFrame();
                return;
            }

            _dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => Run(snapshot)));
        }

        private void Run(SVappsLABSnapshot snapshot)
        {
            try
            {
                _handler(snapshot);
            }
            catch (Exception ex)
            {
                Log.Error($"[SnapshotFanout] {_name} failed to process a telemetry frame", ex);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingFrames);
            }
        }

        // The first drop is logged straight away; later ones are summed and logged at most every
        // 5 s while the UI keeps falling behind.
        private void NoteDroppedFrame()
        {
            _droppedFrames++;
            long now = Stopwatch.GetTimestamp();
            if (_lastDropLogTs != 0 && now - _lastDropLogTs < DropLogIntervalTicks)
                return;

            Log.Warning($"[FrameBacklog] {_name}: UI thread behind; dropped {_droppedFrames} telemetry frame(s)");
            _droppedFrames = 0;
            _lastDropLogTs = now;
        }
    }
}
