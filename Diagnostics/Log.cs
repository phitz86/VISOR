using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VISOR.Diagnostics
{
    /// <summary>
    /// Centralized logging system for VISOR application.
    /// Provides thread-safe, asynchronous file logging with automatic size management.
    /// </summary>
    public static class Log
    {
        private const long MAX_LOG_SIZE_BYTES = 10 * 1024 * 1024; // 10MB
        private const double TRUNCATE_KEEP_PERCENTAGE = 0.8; // Keep 80% of most recent entries
        private const string LOG_FOLDER_NAME = "Logs";
        private const string LOG_FILE_PREFIX = "VISOR_";
        private const string LOG_FILE_EXTENSION = ".log";

        private static readonly BlockingCollection<string> _logQueue = new BlockingCollection<string>();
        private static readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private static Task _writerTask;
        private static string _currentLogFilePath;
        private static readonly object _fileLock = new object();
        private static bool _isInitialized = false;

        /// <summary>
        /// Minimum log level to record. Messages below this level are ignored.
        /// </summary>
        public static Level MinimumLevel { get; set; } = Level.Info;

        /// <summary>
        /// Enable or disable writing logs to file.
        /// </summary>
        public static bool EnableFileLogging { get; set; } = true;

        /// <summary>
        /// Enable or disable Debug.WriteLine output for all log messages.
        /// </summary>
        public static bool EnableDebugOutput { get; set; } = true;

        /// <summary>
        /// Convenience property that sets MinimumLevel to Debug when true, Info when false.
        /// </summary>
        public static bool DebugModeEnabled
        {
            get => MinimumLevel == Level.Debug;
            set => MinimumLevel = value ? Level.Debug : Level.Info;
        }

        /// <summary>
        /// Log levels in order of severity.
        /// </summary>
        public enum Level
        {
            Debug = 0,
            Info = 1,
            Warning = 2,
            Error = 3
        }

        static Log()
        {
            Initialize();
        }

        private static void Initialize()
        {
            if (_isInitialized)
                return;

            try
            {
                Directory.CreateDirectory(GetLogsDirectory());
                _writerTask = Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to initialize logging system", ex);
            }
        }

        /// <summary>
        /// Starts a new logging session with a new log file.
        /// </summary>
        public static void StartNewSession()
        {
            try
            {
                lock (_fileLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string fileName = $"{LOG_FILE_PREFIX}{timestamp}{LOG_FILE_EXTENSION}";
                    _currentLogFilePath = Path.Combine(GetLogsDirectory(), fileName);

                    WriteSessionHeader();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to start new logging session", ex);
            }
        }

        private static void WriteSessionHeader()
        {
            var header = new StringBuilder();
            header.AppendLine("=== VISOR Logging Session Started ===");
            header.AppendLine($"Timestamp Format: YYYYMMDD HH:mm:ss.milliseconds");
            header.AppendLine($"Machine: {Environment.MachineName}");
            header.AppendLine($"OS: {Environment.OSVersion}");
            header.AppendLine("=======================================");

            // Write directly instead of queueing so the header always lands first.
            lock (_fileLock)
            {
                if (EnableFileLogging && !string.IsNullOrEmpty(_currentLogFilePath))
                {
                    File.AppendAllText(_currentLogFilePath, header.ToString());
                }
            }
        }

        /// <summary>
        /// Log a debug message (verbose information for troubleshooting).
        /// </summary>
        public static void Debug(string message)
        {
            LogMessage(Level.Debug, message);
        }

        /// <summary>
        /// Log an informational message (normal operations, state changes).
        /// </summary>
        public static void Info(string message)
        {
            LogMessage(Level.Info, message);
        }

        /// <summary>
        /// Log a warning message (unexpected but recoverable situations).
        /// </summary>
        public static void Warning(string message)
        {
            LogMessage(Level.Warning, message);
        }

        /// <summary>
        /// Log an error message with optional exception details.
        /// </summary>
        public static void Error(string message, Exception ex = null)
        {
            string fullMessage = message;

            if (ex != null)
            {
                fullMessage += $"\n  Exception Type: {ex.GetType().Name}";
                fullMessage += $"\n  Exception Message: {ex.Message}";

                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    fullMessage += "\n  Stack Trace:";
                    var stackLines = ex.StackTrace.Split('\n');
                    foreach (var line in stackLines)
                    {
                        fullMessage += $"\n    {line.TrimEnd()}";
                    }
                }
            }

            LogMessage(Level.Error, fullMessage);
        }

        private static void LogMessage(Level level, string message)
        {
            try
            {
                if (level < MinimumLevel)
                    return;

                string timestamp = DateTime.Now.ToString("yyyyMMdd HH:mm:ss.fff");
                string levelStr = level.ToString().ToUpper().PadRight(7);
                string logEntry = $"[{timestamp}] [{levelStr}] {message}";

                if (EnableDebugOutput)
                {
                    System.Diagnostics.Debug.WriteLine(logEntry);
                }

                if (EnableFileLogging && !string.IsNullOrEmpty(_currentLogFilePath))
                {
                    _logQueue.Add(logEntry);
                }
            }
            catch (Exception ex)
            {
                // Never let logging failures cascade, but surface them to the debugger.
                System.Diagnostics.Debug.WriteLine($"[Log] LogMessage failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ProcessLogQueue(CancellationToken cancellationToken)
        {
            try
            {
                foreach (var logEntry in _logQueue.GetConsumingEnumerable(cancellationToken))
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        lock (_fileLock)
                        {
                            if (!string.IsNullOrEmpty(_currentLogFilePath))
                            {
                                File.AppendAllText(_currentLogFilePath, logEntry + Environment.NewLine);

                                var fileInfo = new FileInfo(_currentLogFilePath);
                                if (fileInfo.Exists && fileInfo.Length > MAX_LOG_SIZE_BYTES)
                                {
                                    TruncateLogFile();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Log] Write failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static void TruncateLogFile()
        {
            // Runs inside _fileLock. Two passes:
            //   1. Stream through the file counting lines.
            //   2. Stream again, writing the last TRUNCATE_KEEP_PERCENTAGE portion to a temp file.
            // Finally swap the temp file over the original with File.Replace.
            string tempPath = _currentLogFilePath + ".tmp";

            try
            {
                long totalLines = 0;
                using (var counter = new StreamReader(_currentLogFilePath))
                {
                    while (counter.ReadLine() != null) totalLines++;
                }

                long linesToSkip = totalLines - (long)(totalLines * TRUNCATE_KEEP_PERCENTAGE);
                if (linesToSkip <= 0) return;

                using (var reader = new StreamReader(_currentLogFilePath))
                using (var writer = new StreamWriter(tempPath, false, Encoding.UTF8))
                {
                    writer.WriteLine("[SYSTEM] === LOG TRUNCATED - KEEPING RECENT ENTRIES ===");

                    long skipped = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (skipped < linesToSkip)
                        {
                            skipped++;
                            continue;
                        }
                        writer.WriteLine(line);
                    }
                }

                File.Replace(tempPath, _currentLogFilePath, destinationBackupFileName: null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Log] TruncateLogFile failed: {ex.GetType().Name}: {ex.Message}");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        /// <summary>
        /// Gets the path to the current log file.
        /// </summary>
        public static string GetCurrentLogPath()
        {
            return _currentLogFilePath ?? string.Empty;
        }

        /// <summary>
        /// Gets the path to the Logs directory.
        /// </summary>
        public static string GetLogsDirectory()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appDataPath, "VISOR", LOG_FOLDER_NAME);
        }

        /// <summary>
        /// Gets the path to the per-user diagnostics directory (%LOCALAPPDATA%\VISOR\Diagnostics).
        /// Use this for ad-hoc CSV/YAML dumps generated by debug loggers.
        /// </summary>
        public static string GetDiagnosticsDirectory()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "VISOR", "Diagnostics");
        }

        /// <summary>
        /// Deletes old log files, keeping only the most recent ones.
        /// </summary>
        /// <param name="maxLogsToKeep">Maximum number of log files to retain</param>
        public static void CleanupOldLogs(int maxLogsToKeep = 10)
        {
            try
            {
                string logsDir = GetLogsDirectory();
                if (!Directory.Exists(logsDir))
                    return;

                var logFiles = Directory.GetFiles(logsDir, $"{LOG_FILE_PREFIX}*{LOG_FILE_EXTENSION}")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                foreach (var fileToDelete in logFiles.Skip(maxLogsToKeep))
                {
                    try
                    {
                        fileToDelete.Delete();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Shuts down the logging system, flushing all pending messages.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                _logQueue.CompleteAdding();
                _writerTask?.Wait(TimeSpan.FromSeconds(5));
                _cancellationTokenSource.Cancel();
            }
            catch
            {
            }
        }
    }
}
