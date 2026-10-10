using System;
using System.IO;
using VISOR.Telemetry;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: file paths for the debug loggers, in %LOCALAPPDATA%\VISOR\Diagnostics\{folder}.
    /// </summary>
    internal static class DiagnosticFiles
    {
        /// <summary>
        /// Creates the folder if needed and returns a new {stem}_{yyyyMMdd-HHmmss}{extension} path
        /// in it (with _2, _3... added if that name is taken). The stem is made file-safe the same
        /// way saved shift models are named (<see cref="ShiftModelStore.SafeFileStem"/>).
        /// </summary>
        public static string NewPath(string folder, string? stem, string extension)
        {
            string dir = Path.Combine(Log.GetDiagnosticsDirectory(), folder);
            Directory.CreateDirectory(dir);

            string name = $"{ShiftModelStore.SafeFileStem(stem ?? string.Empty) ?? "car"}_{DateTime.Now:yyyyMMdd-HHmmss}";
            string path = Path.Combine(dir, name + extension);
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(dir, $"{name}_{n}{extension}");
            return path;
        }
    }
#endif
}
