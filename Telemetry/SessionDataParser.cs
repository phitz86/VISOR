using System;
using System.IO;
using System.Linq;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Handles parsing and caching of iRacing session YAML data
    /// </summary>
    public class SessionDataParser
    {
        // YAML-parsed session data cache
        private readonly int[] _cachedCarNumbers = new int[64];
        private readonly string[] _cachedCarClasses = new string[64];
        private readonly string[] _cachedDriverNames = new string[64];
        private string _lastSessionDataHash = string.Empty;
        private string _cachedSessionYaml = string.Empty;
        private readonly object _parseLock = new();

        // Flag to signal when data is ready
        public bool IsDataReady { get; private set; } = false;

        // Public properties for accessing cached data
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
            InitializeArrays();
        }

        /// <summary>
        /// Parse session YAML data and extract driver information
        /// </summary>
        public bool ParseSessionData(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return false;

            try
            {
                lock (_parseLock)
                {
                    // Check if this is actually new data
                    var currentHash = sessionData.GetHashCode().ToString();
                    if (currentHash == _lastSessionDataHash)
                        return false;

                    // Clear existing data
                    ClearArrays();

                    // Parse the YAML
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

                    // Cache the raw YAML and hash
                    _cachedSessionYaml = sessionData;
                    _lastSessionDataHash = currentHash;
                    IsDataReady = true;

                    Console.WriteLine($"[SessionDataParser] Parsed session data: {GetCachedCarCount()} cars found");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SessionDataParser] Error parsing YAML session data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the cached raw YAML data
        /// </summary>
        public string GetCachedSessionYaml()
        {
            lock (_parseLock)
            {
                return _cachedSessionYaml;
            }
        }

        /// <summary>
        /// Check if session data has changed based on hash
        /// </summary>
        public bool HasSessionDataChanged(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return false;

            lock (_parseLock)
            {
                var currentHash = sessionData.GetHashCode().ToString();
                return currentHash != _lastSessionDataHash;
            }
        }

        /// <summary>
        /// Dump session data to a file for debugging
        /// </summary>
        public string DumpSessionData(string sessionData)
        {
            if (string.IsNullOrEmpty(sessionData))
                return "ERROR: No session data to dump";

            try
            {
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string filename = $"session_dump_{timestamp}.yaml";
                string filepath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), filename);

                File.WriteAllText(filepath, sessionData);

                Console.WriteLine($"[SessionDataParser] Session data dumped to: {filepath}");
                return filepath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SessionDataParser] Error dumping session data: {ex.Message}");
                return $"ERROR: {ex.Message}";
            }
        }

        /// <summary>
        /// Clear all cached data
        /// </summary>
        public void ClearCache()
        {
            lock (_parseLock)
            {
                ClearArrays();
                _lastSessionDataHash = string.Empty;
                _cachedSessionYaml = string.Empty;
                IsDataReady = false;
            }
        }

        /// <summary>
        /// Get count of cars with valid data
        /// </summary>
        public int GetCachedCarCount()
        {
            lock (_parseLock)
            {
                return _cachedCarNumbers.Count(n => n > 0);
            }
        }

        /// <summary>
        /// Get driver info for a specific car index
        /// </summary>
        public (string name, int number, string carClass) GetDriverInfo(int carIdx)
        {
            if (carIdx < 0 || carIdx >= 64)
                return (string.Empty, 0, string.Empty);

            lock (_parseLock)
            {
                return (_cachedDriverNames[carIdx], _cachedCarNumbers[carIdx], _cachedCarClasses[carIdx]);
            }
        }

        /// <summary>
        /// Check if we have valid driver data
        /// </summary>
        public bool HasDriverMap()
        {
            lock (_parseLock)
            {
                return IsDataReady && GetCachedCarCount() > 0;
            }
        }

        private void InitializeArrays()
        {
            for (int i = 0; i < 64; i++)
            {
                _cachedCarClasses[i] = string.Empty;
                _cachedDriverNames[i] = string.Empty;
                _cachedCarNumbers[i] = 0;
            }
        }

        private void ClearArrays()
        {
            Array.Fill(_cachedCarNumbers, 0);
            Array.Fill(_cachedCarClasses, string.Empty);
            Array.Fill(_cachedDriverNames, string.Empty);
        }
    }
}