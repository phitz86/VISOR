using System;
using System.IO;
using System.Linq;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Handles parsing and caching of iRacing session YAML data
    /// </summary>
    public class SessionDataParser : VISOR.ViewModels.ISessionDataProvider
    {
        // YAML-parsed session data cache
        private readonly string[] _cachedUserNames = new string[64];
        private readonly string[] _cachedCarNumbers = new string[64];
        private readonly int[] _cachedCarNumberRaw = new int[64];
        private readonly int[] _cachedCarClassIDs = new int[64];
        private readonly bool[] _cachedCarIsAI = new bool[64];
        private readonly int[] _cachedCurDriverIncidentCount = new int[64];
        private int _cachedIncidentLimit = 0;

        private string _lastSessionDataHash = string.Empty;
        private string _cachedSessionYaml = string.Empty;
        private readonly object _parseLock = new();

        // Flag to signal when data is ready
        public bool IsDataReady { get; private set; } = false;

        // Public properties for accessing cached data
        public string[] UserNames
        {
            get { lock (_parseLock) { return (string[])_cachedUserNames.Clone(); } }
        }

        public string[] CarNumbers
        {
            get { lock (_parseLock) { return (string[])_cachedCarNumbers.Clone(); } }
        }

        public int[] CarNumberRaw
        {
            get { lock (_parseLock) { return (int[])_cachedCarNumberRaw.Clone(); } }
        }

        public int[] CarClassIDs
        {
            get { lock (_parseLock) { return (int[])_cachedCarClassIDs.Clone(); } }
        }

        public bool[] CarIsAI
        {
            get { lock (_parseLock) { return (bool[])_cachedCarIsAI.Clone(); } }
        }

        public int[] CurDriverIncidentCount
        {
            get { lock (_parseLock) { return (int[])_cachedCurDriverIncidentCount.Clone(); } }
        }

        public int IncidentLimit
        {
            get { lock (_parseLock) { return _cachedIncidentLimit; } }
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

                        // Parse IncidentLimit (session-level setting)
                        if (line.StartsWith("IncidentLimit:"))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                if (int.TryParse(parts[1].Trim(), out int incidentLimit))
                                    _cachedIncidentLimit = incidentLimit;
                            }
                        }
                        else if (line.StartsWith("- CarIdx:"))
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
                                    _cachedUserNames[currentCarIdx] = parts[1].Trim().Trim('"');
                            }
                            else if (line.StartsWith("CarNumber:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    _cachedCarNumbers[currentCarIdx] = parts[1].Trim().Trim('"');
                                }
                            }
                            else if (line.StartsWith("CarNumberRaw:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    if (int.TryParse(parts[1].Trim(), out int carNumberRaw))
                                        _cachedCarNumberRaw[currentCarIdx] = carNumberRaw;
                                }
                            }
                            else if (line.StartsWith("CarClassID:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    if (int.TryParse(parts[1].Trim(), out int classId))
                                        _cachedCarClassIDs[currentCarIdx] = classId;
                                }
                            }
                            else if (line.StartsWith("CarIsAI:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    var aiValue = parts[1].Trim();
                                    _cachedCarIsAI[currentCarIdx] = aiValue == "1" || aiValue.ToLower() == "true";
                                }
                            }
                            else if (line.StartsWith("CurDriverIncidentCount:"))
                            {
                                var parts = line.Split(':', 2);
                                if (parts.Length >= 2)
                                {
                                    if (int.TryParse(parts[1].Trim(), out int incidentCount))
                                        _cachedCurDriverIncidentCount[currentCarIdx] = incidentCount;
                                }
                            }
                        }
                    }

                    // Cache the raw YAML and hash
                    _cachedSessionYaml = sessionData;
                    _lastSessionDataHash = currentHash;
                    IsDataReady = true;

                    Console.WriteLine($"[SessionDataParser] Parsed session data: {GetCachedCarCount()} cars found, IncidentLimit: {_cachedIncidentLimit}");
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
                return _cachedCarNumbers.Count(n => !string.IsNullOrEmpty(n));
            }
        }

        /// <summary>
        /// Get driver info for a specific car index
        /// </summary>
        public (string userName, string carNumber, int carNumberRaw, int classId, bool isAI, int incidentCount) GetDriverInfo(int carIdx)
        {
            if (carIdx < 0 || carIdx >= 64)
                return (string.Empty, string.Empty, 0, 0, false, 0);

            lock (_parseLock)
            {
                return (
                    _cachedUserNames[carIdx],
                    _cachedCarNumbers[carIdx],
                    _cachedCarNumberRaw[carIdx],
                    _cachedCarClassIDs[carIdx],
                    _cachedCarIsAI[carIdx],
                    _cachedCurDriverIncidentCount[carIdx]
                );
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
                _cachedUserNames[i] = string.Empty;
                _cachedCarNumbers[i] = string.Empty;
                _cachedCarNumberRaw[i] = 0;
                _cachedCarClassIDs[i] = 0;
                _cachedCarIsAI[i] = false;
                _cachedCurDriverIncidentCount[i] = 0;
            }
            _cachedIncidentLimit = 0;
        }

        private void ClearArrays()
        {
            Array.Fill(_cachedUserNames, string.Empty);
            Array.Fill(_cachedCarNumbers, string.Empty);
            Array.Fill(_cachedCarNumberRaw, 0);
            Array.Fill(_cachedCarClassIDs, 0);
            Array.Fill(_cachedCarIsAI, false);
            Array.Fill(_cachedCurDriverIncidentCount, 0);
            _cachedIncidentLimit = 0;
        }
    }
}