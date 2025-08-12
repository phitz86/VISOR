using System;
using System.IO;

namespace VISOR.Telemetry
{
    // Handles parsing and caching of iRacing session YAML data
    public class SessionDataParser
    {
        // YAML-parsed session data cache
        private readonly int[] _cachedCarNumbers = new int[64]; // iRacing supports max 64 cars
        private readonly string[] _cachedCarClasses = new string[64]; // Car classes from YAML
        private string _lastSessionDataHash = string.Empty;
        private readonly object _parseLock = new();

        // Gets a copy of the cached car numbers array
        public int[] CarNumbers
        {
            get
            {
                lock (_parseLock)
                {
                    return (int[])_cachedCarNumbers.Clone();
                }
            }
        }

        // Gets a copy of the cached car classes array
        public string[] CarClasses
        {
            get
            {
                lock (_parseLock)
                {
                    return (string[])_cachedCarClasses.Clone();
                }
            }
        }

        public SessionDataParser()
        {
            // Initialize car classes array
            for (int i = 0; i < _cachedCarClasses.Length; i++)
            {
                _cachedCarClasses[i] = string.Empty;
            }
        }

        // Parse YAML session data and update cached car information
        public bool ParseSessionData(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return false;

            try
            {
                lock (_parseLock)
                {
                    // Simple hash check to avoid re-parsing identical session data
                    var currentHash = sessionData.GetHashCode().ToString();
                    if (currentHash == _lastSessionDataHash)
                        return false;

                    // Reset the arrays
                    Array.Fill(_cachedCarNumbers, 0);
                    Array.Fill(_cachedCarClasses, string.Empty);

                    // Parse car data from YAML using simple string parsing
                    // Look for patterns like: "- CarIdx: 0" followed by "CarNumber: "42"" and "CarClassShortName: "GT3""
                    var lines = sessionData.Split('\n');
                    int currentCarIdx = -1;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();

                        // Look for CarIdx entries
                        if (line.StartsWith("- CarIdx:"))
                        {
                            var parts = line.Split(':');
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out currentCarIdx))
                            {
                                // Valid CarIdx found, continue to next lines to find CarNumber and CarClass
                                continue;
                            }
                        }

                        // Look for CarNumber entries if we have a valid CarIdx
                        if (currentCarIdx >= 0 && currentCarIdx < 64 && line.StartsWith("CarNumber:"))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                var carNumberStr = parts[1].Trim().Trim('"'); // Remove quotes
                                if (int.TryParse(carNumberStr, out int carNumber))
                                {
                                    _cachedCarNumbers[currentCarIdx] = carNumber;
                                }
                            }
                        }

                        // Look for CarClassShortName entries if we have a valid CarIdx
                        if (currentCarIdx >= 0 && currentCarIdx < 64 && line.StartsWith("CarClassShortName:"))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                var carClass = parts[1].Trim().Trim('"'); // Remove quotes
                                _cachedCarClasses[currentCarIdx] = carClass;
                            }
                            // Reset CarIdx after processing both CarNumber and CarClass
                            currentCarIdx = -1;
                        }
                    }

                    _lastSessionDataHash = currentHash;
                    Console.WriteLine($"[SessionDataParser] Parsed session data: Updated car numbers and classes");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SessionDataParser] Error parsing YAML session data: {ex.Message}");
                return false;
            }
        }

        // Dump the latest session YAML data to a file
        public string DumpSessionData(string sessionData)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionData))
                {
                    return "NO_YAML";
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var filename = $"session_dump_{timestamp}.yaml";
                var filepath = Path.Combine(AppContext.BaseDirectory, "Raw outputs", filename);

                Directory.CreateDirectory(Path.GetDirectoryName(filepath));
                File.WriteAllText(filepath, sessionData);

                return filepath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SessionDataParser] Error dumping YAML: {ex.Message}");
                return "ERROR";
            }
        }

        // Clear all cached data
        public void ClearCache()
        {
            lock (_parseLock)
            {
                Array.Fill(_cachedCarNumbers, 0);
                Array.Fill(_cachedCarClasses, string.Empty);
                _lastSessionDataHash = string.Empty;
            }
        }

        // Get the number of cars currently cached
        public int GetCachedCarCount()
        {
            lock (_parseLock)
            {
                int count = 0;
                for (int i = 0; i < _cachedCarNumbers.Length; i++)
                {
                    if (_cachedCarNumbers[i] > 0)
                        count++;
                }
                return count;
            }
        }
    }
}