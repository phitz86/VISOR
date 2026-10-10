using SVappsLAB.iRacingTelemetrySDK;
using VISOR.Diagnostics;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Decides whether VISOR reads session info itself because the SDK can't
    /// (<see cref="SessionInfoYaml"/>).
    ///
    /// Each raw update is checked first. While the SDK can read it, nothing changes: the SDK's
    /// result is applied as before. The first update the SDK can't read switches VISOR to reading
    /// every update itself until iRacing disconnects. The two never alternate, so a slow result
    /// from one can't land after a newer one from the other.
    ///
    /// <see cref="Read"/> runs on the SDK's raw session-info thread. <see cref="Activate"/> and
    /// <see cref="Reset"/> are called under the wrapper's session-apply lock.
    /// </summary>
    internal sealed class SessionInfoFallback
    {
        private volatile bool _active;
        private string _lastLoggedFailure = string.Empty;

        /// <summary>True while VISOR is reading session info itself (until the next disconnect).</summary>
        public bool IsActive => _active;

        /// <summary>
        /// Returns the session info VISOR should apply from this update, or null when it is the
        /// SDK's to handle, or when not even the repair could read it.
        /// </summary>
        /// <param name="wellFormed">The update passed <see cref="SessionInfoYaml.IsWellFormed"/>.</param>
        public TelemetrySessionInfo? Read(string yaml, bool wellFormed)
        {
            if (!_active && (wellFormed || SessionInfoYaml.IsWellFormed(SessionInfoYaml.QuoteSdkFields(yaml))))
                return null;

            var info = SessionInfoYaml.Parse<TelemetrySessionInfo>(yaml, out string outcome);
            if (info == null && outcome != _lastLoggedFailure)
            {
                _lastLoggedFailure = outcome;
                Log.Warning($"[SessionInfo] Session info could not be read, even repaired ({outcome})");
            }
            return info;
        }

        /// <summary>VISOR's own result is about to be applied: from now on the SDK's are ignored.</summary>
        public void Activate()
        {
            if (_active)
                return;
            _active = true;
            Log.Warning("[SessionInfo] The SDK could not read this session's info, so VISOR is reading it " +
                        "itself until iRacing disconnects. The SDK's own errors for it are logged at Debug level meanwhile.");
        }

        /// <summary>iRacing disconnected: the next session starts with the SDK's parse again.</summary>
        public void Reset()
        {
            _active = false;
            _lastLoggedFailure = string.Empty;
        }
    }
}
