using Microsoft.Extensions.Logging;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Bridges Microsoft.Extensions.Logging output from the SVappsLAB SDK into VISOR's Log.cs.
    /// Warnings and errors are always surfaced; Info/Debug are forwarded when VISOR debug mode is on.
    /// </summary>
    public class VisorSdkLogger<T> : ILogger<T>
    {
        // The SDK's Debug lines can repeat dozens of times a second (e.g. "timeout waiting for data
        // ready event" every 30 ms while iRacing sends nothing), so each one is logged at most once
        // a second, with a count of the repeats held back.
        private readonly Diagnostics.LogRateLimiter _debugLimiter = new(TimeSpan.FromSeconds(1));

        // Set while VISOR reads session info itself (SessionInfoFallback): the SDK's session-info
        // parse warnings and errors then repeat on every update, so they go to Debug instead.
        private volatile bool _quietSessionInfoErrors;
        public bool QuietSessionInfoErrors
        {
            get => _quietSessionInfoErrors;
            set => _quietSessionInfoErrors = value;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning || Diagnostics.Log.DebugModeEnabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (formatter == null) return;

            int suppressed = 0;
            if (logLevel < LogLevel.Information && !_debugLimiter.TryPass(TemplateOf(state), out suppressed))
                return;

            string message = $"[SDK] {formatter(state, exception)}";
            if (suppressed > 0) message += $" (+{suppressed} more like this since the last one shown)";

            if (_quietSessionInfoErrors && logLevel >= LogLevel.Warning &&
                (message.Contains("YAML parse failed") || message.Contains("sessionTelemetryInfo")))
            {
                Diagnostics.Log.Debug(exception == null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})");
                return;
            }

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

        // The message template ("required {attempts} attempts"), so repeats with different values
        // count as the same line.
        private static string TemplateOf<TState>(TState state)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var kv in values)
                    if (kv.Key == "{OriginalFormat}" && kv.Value is string template) return template;
            }
            return state?.ToString() ?? string.Empty;
        }
    }
}
