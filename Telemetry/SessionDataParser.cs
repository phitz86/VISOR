using System;
using System.IO;
using System.Linq;

namespace VISOR.Telemetry
{
    // Handles parsing and caching of iRacing session YAML data
    public class SessionDataParser
    {
        // YAML-parsed session data cache
        private readonly int[] _cachedCarNumbers = new int[64];
        private readonly string[] _cachedCarClasses = new string[64];
        private readonly string[] _cachedDriverNames = new string[64];
        private string _lastSessionDataHash = string.Empty;
        private readonly object _parseLock = new();

        // New flag to signal when data is ready.
        public bool IsDataReady { get; private set; } = false;

        public int[] CarNumbers
        {
            get { lock (_parseLock) { return (int[])_cachedCarNumbers.Clone(); } }
        }

        public string[] CarClasses
        {
            get { lock (_parseLock) { return (string[])_cachedCarClasses.Clone(); } }
        }

        public string[] DriverNames
        {
            get { lock (_parseLock) { return (string[])_cachedDriverNames.Clone(); } }
        }

        public SessionDataParser()
        {
            for (int i = 0; i < 64; i++)
            {
                _cachedCarClasses[i] = string.Empty;
                _cachedDriverNames[i] = string.Empty;
            }
        }

        public bool ParseSessionData(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return false;

            try
            {
                lock (_parseLock)
                {
                    var currentHash = sessionData.GetHashCode().ToString();
                    if (currentHash == _lastSessionDataHash)
                        return false;

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
                                    _cachedDriverNames[currentCarIdx] = parts[1].Trim().Trim('"');
                            }
                            else if (line.StartsWith("CarNumber:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    var carNumberStr = parts[1].Trim().Trim('"');
                                    if (int.TryParse(carNumberStr, out int carNumber))
                                        _cachedCarNumbers[currentCarIdx] = carNumber;
                                }
                            }
                            else if (line.StartsWith("CarClassShortName:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                    _cachedCarClasses[currentCarIdx] = parts[1].Trim().Trim('"');
                            }
                        }
                    }

                    _lastSessionDataHash = currentHash;
                    IsDataReady = true; // Set the flag to true on successful parse.
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

        public string DumpSessionData(string sessionData)
        {
            // ... (method unchanged)
            return "ERROR";
        }

        public void ClearCache()
        {
            lock (_parseLock)
            {
                Array.Fill(_cachedCarNumbers, 0);
                Array.Fill(_cachedCarClasses, string.Empty);
                Array.Fill(_cachedDriverNames, string.Empty);
                _lastSessionDataHash = string.Empty;
                IsDataReady = false; // Reset the flag.
            }
        }

        public int GetCachedCarCount()
        {
            lock (_parseLock)
            {
                return _cachedCarNumbers.Count(n => n > 0);
            }
        }
    }
}
