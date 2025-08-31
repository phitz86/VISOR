using System;
using System.Collections.Generic;
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

        // Session-specific data
        private string _cachedSessionType = string.Empty;
        private string _cachedSessionName = string.Empty;
        private readonly int[] _cachedQualifyPositions = new int[64]; // Position by CarIdx
        private readonly float[] _cachedQualifyFastestTimes = new float[64]; // Fastest time by CarIdx

        // NEW: Session schedule parsing
        private int _cachedCurrentSessionNum = -1;
        private readonly Dictionary<int, string> _sessionSchedule = new(); // SessionNum -> SessionType
        private readonly Dictionary<int, string> _sessionNames = new(); // SessionNum -> SessionName
        private readonly Dictionary<int, int> _sessionLaps = new(); // SessionNum -> SessionLaps (-1 = unlimited)
        private readonly Dictionary<int, double> _sessionTimes = new(); // SessionNum -> SessionTime (seconds)

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

        // Legacy session data accessors (for backward compatibility)
        public string SessionType
        {
            get { lock (_parseLock) { return _cachedSessionType; } }
        }

        public string SessionName
        {
            get { lock (_parseLock) { return _cachedSessionName; } }
        }

        public int[] QualifyResultsPositions
        {
            get { lock (_parseLock) { return (int[])_cachedQualifyPositions.Clone(); } }
        }

        public float[] QualifyResultsFastestTimes
        {
            get { lock (_parseLock) { return (float[])_cachedQualifyFastestTimes.Clone(); } }
        }

        // NEW: Session schedule accessors
        public int CurrentSessionNum
        {
            get { lock (_parseLock) { return _cachedCurrentSessionNum; } }
        }

        public string GetSessionTypeForNum(int sessionNum)
        {
            lock (_parseLock)
            {
                return _sessionSchedule.GetValueOrDefault(sessionNum, "Unknown");
            }
        }

        public string GetSessionNameForNum(int sessionNum)
        {
            lock (_parseLock)
            {
                return _sessionNames.GetValueOrDefault(sessionNum, "Unknown");
            }
        }

        public bool IsLoneQualifying(int sessionNum)
        {
            return GetSessionTypeForNum(sessionNum).Contains("Lone");
        }

        public bool IsPracticeSession(int sessionNum)
        {
            var sessionType = GetSessionTypeForNum(sessionNum);
            return sessionType.Contains("Practice") || sessionType.Contains("PRACTICE");
        }

        public bool IsQualifyingSession(int sessionNum)
        {
            var sessionType = GetSessionTypeForNum(sessionNum);
            return sessionType.Contains("Qualify") || sessionType.Contains("QUALIFY");
        }

        public bool IsRaceSession(int sessionNum)
        {
            var sessionType = GetSessionTypeForNum(sessionNum);
            return sessionType.Contains("Race") || sessionType.Contains("RACE");
        }

        public int GetSessionLaps(int sessionNum)
        {
            lock (_parseLock)
            {
                return _sessionLaps.GetValueOrDefault(sessionNum, -1);
            }
        }

        public double GetSessionTimeSeconds(int sessionNum)
        {
            lock (_parseLock)
            {
                return _sessionTimes.GetValueOrDefault(sessionNum, 0.0);
            }
        }

        public Dictionary<int, string> GetSessionSchedule()
        {
            lock (_parseLock)
            {
                return new Dictionary<int, string>(_sessionSchedule);
            }
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
                    int currentSessionIdx = -1;
                    bool inSessionInfo = false;
                    bool inQualifyResults = false;
                    bool inSessionsArray = false;
                    int currentQualifyCarIdx = -1;

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
                        // Parse session info section
                        else if (line.StartsWith("SessionInfo:"))
                        {
                            inSessionInfo = true;
                            Console.WriteLine("[SessionParser DEBUG] Found SessionInfo section");
                        }
                        // NEW: Parse CurrentSessionNum
                        else if (inSessionInfo && line.StartsWith("CurrentSessionNum:"))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int currentSessionNum))
                            {
                                _cachedCurrentSessionNum = currentSessionNum;
                                Console.WriteLine($"[SessionParser DEBUG] CurrentSessionNum: {_cachedCurrentSessionNum}");
                            }
                        }
                        // NEW: Detect Sessions array
                        else if (inSessionInfo && line.StartsWith("Sessions:"))
                        {
                            inSessionsArray = true;
                            Console.WriteLine("[SessionParser DEBUG] Found Sessions array");
                        }
                        // NEW: Parse individual session entries
                        else if (inSessionsArray && line.StartsWith("- SessionNum:"))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int sessionNum))
                            {
                                currentSessionIdx = sessionNum;
                                Console.WriteLine($"[SessionParser DEBUG] Found session definition for SessionNum {currentSessionIdx}");
                            }
                        }
                        else if (inSessionsArray && line.StartsWith("SessionType:") && currentSessionIdx >= 0)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                var sessionType = parts[1].Trim();
                                _sessionSchedule[currentSessionIdx] = sessionType;
                                Console.WriteLine($"[SessionParser DEBUG] Session {currentSessionIdx} type: {sessionType}");
                            }
                        }
                        else if (inSessionsArray && line.StartsWith("SessionName:") && currentSessionIdx >= 0)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                var sessionName = parts[1].Trim();
                                _sessionNames[currentSessionIdx] = sessionName;
                                Console.WriteLine($"[SessionParser DEBUG] Session {currentSessionIdx} name: {sessionName}");
                            }
                        }
                        else if (inSessionsArray && line.StartsWith("SessionLaps:") && currentSessionIdx >= 0)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                var lapsText = parts[1].Trim();
                                if (lapsText == "unlimited")
                                {
                                    _sessionLaps[currentSessionIdx] = -1;
                                }
                                else if (int.TryParse(lapsText, out int laps))
                                {
                                    _sessionLaps[currentSessionIdx] = laps;
                                }
                                Console.WriteLine($"[SessionParser DEBUG] Session {currentSessionIdx} laps: {lapsText}");
                            }
                        }
                        else if (inSessionsArray && line.StartsWith("SessionTime:") && currentSessionIdx >= 0)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                var timeText = parts[1].Trim().Replace(" sec", "");
                                if (double.TryParse(timeText, out double timeSeconds))
                                {
                                    _sessionTimes[currentSessionIdx] = timeSeconds;
                                    Console.WriteLine($"[SessionParser DEBUG] Session {currentSessionIdx} time: {timeSeconds}s");
                                }
                            }
                        }
                        // Legacy session type parsing (for backward compatibility)
                        else if (inSessionInfo && line.StartsWith("- SessionNum:"))
                        {
                            // We're in the active session data
                            Console.WriteLine("[SessionParser DEBUG] Found active session data");
                        }
                        else if (inSessionInfo && line.StartsWith("SessionType:") && !inSessionsArray)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                _cachedSessionType = parts[1].Trim();
                                Console.WriteLine($"[SessionParser DEBUG] Legacy SessionType: {_cachedSessionType}");
                            }
                        }
                        else if (inSessionInfo && line.StartsWith("SessionName:") && !inSessionsArray)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2)
                            {
                                _cachedSessionName = parts[1].Trim();
                                Console.WriteLine($"[SessionParser DEBUG] Legacy SessionName: {_cachedSessionName}");
                            }
                        }
                        // Parse qualifying results section
                        else if (line.StartsWith("QualifyResultsInfo:"))
                        {
                            inQualifyResults = true;
                            inSessionInfo = false; // Exit SessionInfo parsing
                            inSessionsArray = false;
                            Console.WriteLine("[SessionParser DEBUG] Found QualifyResultsInfo section");
                        }
                        else if (inQualifyResults && line.StartsWith("- Position:"))
                        {
                            // Start of a new qualify result entry
                            currentQualifyCarIdx = -1;
                        }
                        else if (inQualifyResults && line.StartsWith("CarIdx:"))
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out currentQualifyCarIdx))
                            {
                                Console.WriteLine($"[SessionParser DEBUG] QualifyResults CarIdx: {currentQualifyCarIdx}");
                            }
                        }
                        else if (inQualifyResults && line.StartsWith("Position:") && currentQualifyCarIdx >= 0 && currentQualifyCarIdx < 64)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out int position))
                            {
                                _cachedQualifyPositions[currentQualifyCarIdx] = position;
                                Console.WriteLine($"[SessionParser DEBUG] Car {currentQualifyCarIdx} qualify position: {position}");
                            }
                        }
                        else if (inQualifyResults && line.StartsWith("FastestTime:") && currentQualifyCarIdx >= 0 && currentQualifyCarIdx < 64)
                        {
                            var parts = line.Split(':', 2);
                            if (parts.Length >= 2 && float.TryParse(parts[1].Trim(), out float fastestTime))
                            {
                                _cachedQualifyFastestTimes[currentQualifyCarIdx] = fastestTime;
                                Console.WriteLine($"[SessionParser DEBUG] Car {currentQualifyCarIdx} fastest time: {fastestTime}");
                            }
                        }
                        // Driver info parsing (existing logic)
                        else if (line.StartsWith("- CarIdx:") && !inSessionsArray)
                        {
                            var parts = line.Split(':');
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out currentCarIdx))
                            {
                                continue;
                            }
                        }

                        if (currentCarIdx >= 0 && currentCarIdx < 64 && !inSessionsArray)
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

                    // Debug session summary
                    int humanDrivers = _cachedCarIsAI.Count(ai => !ai);
                    int aiDrivers = _cachedCarIsAI.Count(ai => ai);
                    int validQualifyEntries = _cachedQualifyFastestTimes.Count(t => t > 0);

                    Console.WriteLine($"[SessionParser DEBUG] === SESSION SUMMARY ===");
                    Console.WriteLine($"[SessionParser DEBUG] Current Session: {_cachedCurrentSessionNum}");
                    Console.WriteLine($"[SessionParser DEBUG] Session Schedule:");
                    foreach (var kvp in _sessionSchedule.OrderBy(x => x.Key))
                    {
                        var sessionName = _sessionNames.GetValueOrDefault(kvp.Key, "Unknown");
                        var sessionLaps = _sessionLaps.GetValueOrDefault(kvp.Key, -1);
                        var sessionTime = _sessionTimes.GetValueOrDefault(kvp.Key, 0.0);
                        var lapsText = sessionLaps == -1 ? "unlimited" : sessionLaps.ToString();
                        Console.WriteLine($"[SessionParser DEBUG]   SessionNum {kvp.Key}: {kvp.Value} ({sessionName}) - {lapsText} laps, {sessionTime}s");
                    }
                    Console.WriteLine($"[SessionParser DEBUG] Total cars: {GetCachedCarCount()}");
                    Console.WriteLine($"[SessionParser DEBUG] Human drivers: {humanDrivers}");
                    Console.WriteLine($"[SessionParser DEBUG] AI drivers: {aiDrivers}");
                    Console.WriteLine($"[SessionParser DEBUG] Qualify entries with times: {validQualifyEntries}");
                    Console.WriteLine($"[SessionParser DEBUG] Incident limit: {_cachedIncidentLimit}");
                    Console.WriteLine($"[SessionParser DEBUG] ========================");

                    // Also write to VS Debug Output
                    System.Diagnostics.Debug.WriteLine($"[SessionParser] Current Session: {_cachedCurrentSessionNum}, Schedule: {_sessionSchedule.Count} sessions");

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
                _cachedQualifyPositions[i] = 0;
                _cachedQualifyFastestTimes[i] = 0f;
            }
            _cachedIncidentLimit = 0;
            _cachedSessionType = string.Empty;
            _cachedSessionName = string.Empty;
            _cachedCurrentSessionNum = -1;
        }

        private void ClearArrays()
        {
            Array.Fill(_cachedUserNames, string.Empty);
            Array.Fill(_cachedCarNumbers, string.Empty);
            Array.Fill(_cachedCarNumberRaw, 0);
            Array.Fill(_cachedCarClassIDs, 0);
            Array.Fill(_cachedCarIsAI, false);
            Array.Fill(_cachedCurDriverIncidentCount, 0);
            Array.Fill(_cachedQualifyPositions, 0);
            Array.Fill(_cachedQualifyFastestTimes, 0f);
            _cachedIncidentLimit = 0;
            _cachedSessionType = string.Empty;
            _cachedSessionName = string.Empty;
            _cachedCurrentSessionNum = -1;
            _sessionSchedule.Clear();
            _sessionNames.Clear();
            _sessionLaps.Clear();
            _sessionTimes.Clear();
        }
    }
}