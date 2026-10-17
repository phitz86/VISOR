using System;
using System.Linq;
using SVappsLAB.iRacingTelemetrySDK;
using VISOR.Diagnostics;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Holds the latest session info as one read-only <see cref="SessionView"/>. Each update that
    /// reads correctly is built into a new view and published with a single reference write;
    /// readers take <see cref="Current"/> once per frame and use that view throughout, so they
    /// never see half of one update and half of the next, and never take a lock.
    /// </summary>
    public class SessionDataCoordinator
    {
        // Writers only (readers never lock): an update being published and a disconnect clearing
        // it take turns, so the published view and the last logged summary always match.
        private readonly object _publishLock = new();

        private volatile SessionView _current = SessionView.Empty;
        private string _lastParseSummary = string.Empty;

        /// <summary>
        /// The latest session info: <see cref="SessionView.Empty"/> until the first update and
        /// after a disconnect. Read it once per frame and keep the reference.
        /// </summary>
        public SessionView Current => _current;

        public bool IsDataReady => _current.IsDataReady;

        /// <summary>
        /// Publish a new view built from this update. An update with no drivers or no sessions
        /// isn't published: the previous view stays, whole.
        /// </summary>
        public bool ApplySdkSession(TelemetrySessionInfo info)
        {
            if (info == null) return false;

            try
            {
                var view = SessionViewBuilder.Build(info);
                if (!view.IsDataReady) return false;

                lock (_publishLock)
                {
                    _current = view;

                    var trackName = !string.IsNullOrEmpty(view.Track.DisplayName)
                        ? view.Track.DisplayName
                        : view.Track.Name;
                    var sessions = string.Join(", ", view.Sessions.Values.Select(s => s.SessionType));
                    var summary = $"Session data parsed: {trackName}, {view.DriverCount} drivers, sessions: [{sessions}]";
                    if (summary != _lastParseSummary)
                    {
                        Log.Info(summary);
                        _lastParseSummary = summary;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("SessionDataCoordinator ApplySdkSession error", ex);
                return false;
            }
        }

        public void ClearCache()
        {
            lock (_publishLock)
            {
                _current = SessionView.Empty;
                _lastParseSummary = string.Empty;
            }
        }
    }
}
