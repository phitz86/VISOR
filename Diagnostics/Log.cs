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
                // Ensure logs directory exists
                Directory.CreateDirectory(GetLogsDirectory());

                // Start the background writer task
                _writerTask = Task.Run(() => ProcessLogQueue(_cancellationTokenSource.Token));

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                // Critical failure - bubble up
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
                    // Generate new log file path
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string fileName = $"{LOG_FILE_PREFIX}{timestamp}{LOG_FILE_EXTENSION}";
                    _currentLogFilePath = Path.Combine(GetLogsDirectory(), fileName);

                    // Write session header
                    WriteSessionHeader();
                }
            }
            catch (Exception ex)
            {
                // Critical failure - bubble up
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

            // Write header directly to file (not through queue to ensure it's first)
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
                // Check if message should be logged based on minimum level
                if (level < MinimumLevel)
                    return;

                // Format the log entry
                string timestamp = DateTime.Now.ToString("yyyyMMdd HH:mm:ss.fff");
                string levelStr = level.ToString().ToUpper().PadRight(7);
                string logEntry = $"[{timestamp}] [{levelStr}] {message}";

                // Send to Debug output if enabled
                if (EnableDebugOutput)
                {
                    System.Diagnostics.Debug.WriteLine(logEntry);
                }

                // Queue for file writing if enabled
                if (EnableFileLogging && !string.IsNullOrEmpty(_currentLogFilePath))
                {
                    _logQueue.Add(logEntry);
                }
            }
            catch
            {
                // Suppress logging errors to prevent cascading failures
                // This is a non-critical operation
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
                                // Write the log entry
                                File.AppendAllText(_currentLogFilePath, logEntry + Environment.NewLine);

                                // Check if truncation is needed
                                var fileInfo = new FileInfo(_currentLogFilePath);
                                if (fileInfo.Exists && fileInfo.Length > MAX_LOG_SIZE_BYTES)
                                {
                                    TruncateLogFile();
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Suppress individual write errors
                        // Continue processing other messages
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        private static void TruncateLogFile()
        {
            try
            {
                // This runs inside _fileLock, so it's already thread-safe

                // Read all lines from the file
                var allLines = File.ReadAllLines(_currentLogFilePath);

                // Calculate how many lines to keep (80%)
                int linesToKeep = (int)(allLines.Length * TRUNCATE_KEEP_PERCENTAGE);
                int linesToSkip = allLines.Length - linesToKeep;

                // Build new file content
                var newContent = new StringBuilder();
                newContent.AppendLine("[SYSTEM] === LOG TRUNCATED - KEEPING RECENT ENTRIES ===");

                foreach (var line in allLines.Skip(linesToSkip))
                {
                    newContent.AppendLine(line);
                }

                // Write back to file (overwrite)
                File.WriteAllText(_currentLogFilePath, newContent.ToString());
            }
            catch
            {
                // If truncation fails, just continue - better to have a large log than no log
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

                // Delete old files beyond the limit
                foreach (var fileToDelete in logFiles.Skip(maxLogsToKeep))
                {
                    try
                    {
                        fileToDelete.Delete();
                    }
                    catch
                    {
                        // Continue if individual file deletion fails
                    }
                }
            }
            catch
            {
                // Suppress cleanup errors - non-critical operation
            }
        }

        /// <summary>
        /// Shuts down the logging system, flushing all pending messages.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                // Signal shutdown
                _logQueue.CompleteAdding();

                // Wait for queue to drain (with timeout)
                _writerTask?.Wait(TimeSpan.FromSeconds(5));

                // Cancel the writer task
                _cancellationTokenSource.Cancel();
            }
            catch
            {
                // Best effort shutdown
            }
        }
    }
}
