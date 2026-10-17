using Microsoft.Extensions.Logging;
using System;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Bridges Microsoft.Extensions.Logging output from the SVappsLAB SDK into VISOR's Log.cs.
    /// Warnings and errors are always surfaced; Info/Debug are forwarded when VISOR debug mode is on.
    /// </summary>
    public class VisorSdkLogger<T> : ILogger<T>
    {
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

            string message = $"[SDK] {formatter(state, exception)}";

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
    }
}
