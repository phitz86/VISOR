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
        private readonly string[] _cachedDriverNames = new string[64]; // Driver names from YAML
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

        // Gets a copy of the cached driver names array
        public string[] DriverNames
        {
            get
            {
                lock (_parseLock)
                {
                    return (string[])_cachedDriverNames.Clone();
                }
            }
        }

        public SessionDataParser()
        {
            // Initialize arrays
            for (int i = 0; i < 64; i++)
            {
                _cachedCarClasses[i] = string.Empty;
                _cachedDriverNames[i] = string.Empty;
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
                    Array.Fill(_cachedDriverNames, string.Empty);

                    var lines = sessionData.Split('\n');
                    int currentCarIdx = -1;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();

                        if (line.StartsWith("- CarIdx:"))
                        {
                            var parts = line.Split(':');
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out currentCarIdx))
                            {
                                continue;
                            }
                        }

                        if (currentCarIdx >= 0 && currentCarIdx < 64)
                        {
                            if (line.StartsWith("UserName:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    _cachedDriverNames[currentCarIdx] = parts[1].Trim().Trim('"');
                                }
                            }
                            else if (line.StartsWith("CarNumber:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    var carNumberStr = parts[1].Trim().Trim('"');
                                    if (int.TryParse(carNumberStr, out int carNumber))
                                    {
                                        _cachedCarNumbers[currentCarIdx] = carNumber;
                                    }
                                }
                            }
                            else if (line.StartsWith("CarClassShortName:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    _cachedCarClasses[currentCarIdx] = parts[1].Trim().Trim('"');
                                }
                            }
                        }
                    }

                    _lastSessionDataHash = currentHash;
                    Console.WriteLine($"[SessionDataParser] Parsed session data: Updated car numbers, classes, and names.");
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
                Array.Fill(_cachedDriverNames, string.Empty);
                _lastSessionDataHash = string.Empty;
            }
        }
    }
}
