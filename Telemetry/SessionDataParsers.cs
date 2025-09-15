using System;
using System.Linq;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Parses static event data that doesn't change
    /// </summary>
    public class StaticEventParser
    {
        public bool ParseStaticData(string[] lines, StaticEventData eventData)
        {
            int currentCarIdx = -1;
            bool inDriverInfo = false;
            bool inSessionInfo = false;
            bool inSessionsArray = false;
            bool inWeekendInfo = false;
            int currentSessionIdx = -1;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Parse incident limit
                if (trimmed.StartsWith("IncidentLimit:"))
                {
                    if (TryParseIntValue(trimmed, out int limit))
                        eventData.IncidentLimit = limit;
                }
                // Enter weekend info section
                else if (trimmed.StartsWith("WeekendInfo:"))
                {
                    inWeekendInfo = true;
                    inDriverInfo = false;
                    inSessionInfo = false;
                }
                // Parse weekend/track information
                else if (inWeekendInfo)
                {
                    if (trimmed.StartsWith("TrackName:"))
                        eventData.Weekend.TrackName = ParseStringValue(trimmed);
                    else if (trimmed.StartsWith("TrackConfigName:"))
                        eventData.Weekend.TrackConfig = ParseStringValue(trimmed);
                    else if (trimmed.StartsWith("TrackLength:"))
                    {
                        var lengthStr = ParseStringValue(trimmed).Replace(" km", "").Replace(" m", "");
                        if (float.TryParse(lengthStr, out float length))
                        {
                            // Convert to meters if needed
                            if (trimmed.Contains(" km"))
                                length *= 1000f;
                            eventData.Weekend.TrackLength = length;
                        }
                    }
                    else if (trimmed.StartsWith("TrackDisplayName:"))
                        eventData.Weekend.TrackDisplayName = ParseStringValue(trimmed);
                    else if (trimmed.StartsWith("TrackDisplayShortName:"))
                        eventData.Weekend.TrackDisplayShortName = ParseStringValue(trimmed);
                }
                // Enter driver info section
                else if (trimmed.StartsWith("DriverInfo:"))
                {
                    inDriverInfo = true;
                    inWeekendInfo = false;
                    inSessionInfo = false;
                }
                // Parse individual drivers
                else if (inDriverInfo && trimmed.StartsWith("- CarIdx:"))
                {
                    if (TryParseIntValue(trimmed, out int carIdx))
                        currentCarIdx = carIdx;
                }
                else if (inDriverInfo && currentCarIdx >= 0 && currentCarIdx < 64)
                {
                    if (!eventData.Drivers.ContainsKey(currentCarIdx))
                        eventData.Drivers[currentCarIdx] = new StaticEventData.DriverInfo();

                    var driver = eventData.Drivers[currentCarIdx];

                    if (trimmed.StartsWith("UserName:"))
                        driver.UserName = ParseStringValue(trimmed);
                    else if (trimmed.StartsWith("CarNumber:"))
                        driver.CarNumber = ParseStringValue(trimmed);
                    else if (trimmed.StartsWith("CarNumberRaw:") && TryParseIntValue(trimmed, out int carNumRaw))
                        driver.CarNumberRaw = carNumRaw;
                    else if (trimmed.StartsWith("CarClassID:") && TryParseIntValue(trimmed, out int classId))
                        driver.CarClassID = classId;
                    else if (trimmed.StartsWith("CarIsAI:"))
                        driver.IsAI = ParseBoolValue(trimmed);
                }
                // Parse session schedule
                else if (trimmed.StartsWith("SessionInfo:"))
                {
                    inSessionInfo = true;
                    inDriverInfo = false;
                    inWeekendInfo = false;
                }
                else if (inSessionInfo && trimmed.StartsWith("Sessions:"))
                {
                    inSessionsArray = true;
                }
                else if (inSessionsArray && trimmed.StartsWith("- SessionNum:"))
                {
                    if (TryParseIntValue(trimmed, out int sessionNum))
                        currentSessionIdx = sessionNum;
                }
                else if (inSessionsArray && currentSessionIdx >= 0)
                {
                    if (!eventData.Schedule.Sessions.ContainsKey(currentSessionIdx))
                    {
                        eventData.Schedule.Sessions[currentSessionIdx] = new StaticEventData.SessionSchedule.SessionDefinition
                        {
                            SessionNum = currentSessionIdx
                        };
                    }

                    var session = eventData.Schedule.Sessions[currentSessionIdx];

                    if (trimmed.StartsWith("SessionType:"))
                        session.SessionType = ParseStringValue(trimmed);
                    else if (trimmed.StartsWith("SessionName:"))
                        session.SessionName = ParseStringValue(trimmed);
                    else if (trimmed.StartsWith("SessionLaps:"))
                    {
                        var lapsText = ParseStringValue(trimmed);
                        session.SessionLaps = lapsText == "unlimited" ? -1 : int.TryParse(lapsText, out int laps) ? laps : -1;
                    }
                    else if (trimmed.StartsWith("SessionTime:"))
                    {
                        var timeText = ParseStringValue(trimmed).Replace(" sec", "");
                        if (double.TryParse(timeText, out double timeSeconds))
                            session.SessionTimeSeconds = timeSeconds;
                    }
                }
            }

            return eventData.Drivers.Count > 0 && eventData.Schedule.Sessions.Count > 0;
        }

        private bool TryParseIntValue(string line, out int value)
        {
            var parts = line.Split(':', 2);
            value = 0;
            return parts.Length >= 2 && int.TryParse(parts[1].Trim(), out value);
        }

        private string ParseStringValue(string line)
        {
            var parts = line.Split(':', 2);
            return parts.Length >= 2 ? parts[1].Trim().Trim('"') : string.Empty;
        }

        private bool ParseBoolValue(string line)
        {
            var value = ParseStringValue(line);
            return value == "1" || value.ToLower() == "true";
        }
    }

    /// <summary>
    /// Parses session transition data
    /// </summary>
    public class SessionTransitionParser
    {
        public bool ParseTransitionData(string[] lines, SessionTransitionData transitionData, StaticEventData eventData)
        {
            bool inSessionInfo = false;
            bool inDriverInfo = false;
            int currentCarIdx = -1;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("SessionInfo:"))
                {
                    inSessionInfo = true;
                    inDriverInfo = false;
                }
                else if (inSessionInfo && trimmed.StartsWith("CurrentSessionNum:"))
                {
                    if (TryParseIntValue(trimmed, out int sessionNum))
                    {
                        transitionData.CurrentSessionNum = sessionNum;

                        // Set legacy compatibility fields
                        if (eventData.Schedule.Sessions.TryGetValue(sessionNum, out var sessionDef))
                        {
                            transitionData.CurrentSessionType = sessionDef.SessionType;
                            transitionData.CurrentSessionName = sessionDef.SessionName;
                        }
                    }
                }
                else if (trimmed.StartsWith("DriverInfo:"))
                {
                    inDriverInfo = true;
                    inSessionInfo = false;
                }
                else if (inDriverInfo && trimmed.StartsWith("- CarIdx:"))
                {
                    if (TryParseIntValue(trimmed, out int carIdx))
                        currentCarIdx = carIdx;
                }
                else if (inDriverInfo && currentCarIdx >= 0 && trimmed.StartsWith("CurDriverIncidentCount:"))
                {
                    if (TryParseIntValue(trimmed, out int incidentCount))
                        transitionData.DriverIncidentCounts[currentCarIdx] = incidentCount;
                }
            }

            return transitionData.CurrentSessionNum >= 0;
        }

        private bool TryParseIntValue(string line, out int value)
        {
            var parts = line.Split(':', 2);
            value = 0;
            return parts.Length >= 2 && int.TryParse(parts[1].Trim(), out value);
        }
    }

    /// <summary>
    /// Parses live session results data
    /// </summary>
    public class LiveSessionParser
    {
        public bool ParseLiveData(string[] lines, LiveSessionData liveData, int currentSessionNum)
        {
            bool inSessionInfo = false;
            bool inResultsPositions = false;
            bool inResultsFastestLap = false;
            bool inQualifyResults = false;
            int currentSessionIdx = -1;
            int currentQualifyCarIdx = -1;

            // Initialize session data if not exists
            if (!liveData.SessionResultsPositions.ContainsKey(currentSessionNum))
                liveData.SessionResultsPositions[currentSessionNum] = new System.Collections.Generic.List<LiveSessionData.ResultPosition>();
            if (!liveData.SessionFastestLaps.ContainsKey(currentSessionNum))
                liveData.SessionFastestLaps[currentSessionNum] = new System.Collections.Generic.List<LiveSessionData.FastestLapResult>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.StartsWith("SessionInfo:"))
                {
                    inSessionInfo = true;
                }
                else if (inSessionInfo && trimmed.StartsWith("- SessionNum:"))
                {
                    if (TryParseIntValue(trimmed, out int sessionNum))
                        currentSessionIdx = sessionNum;
                }
                else if (inSessionInfo && currentSessionIdx == currentSessionNum && trimmed.StartsWith("ResultsPositions:"))
                {
                    inResultsPositions = true;
                    liveData.SessionResultsPositions[currentSessionNum].Clear();
                }
                else if (inResultsPositions && trimmed.StartsWith("- Position:"))
                {
                    // Start new position entry
                    var position = new LiveSessionData.ResultPosition();
                    if (TryParseIntValue(trimmed, out int pos))
                        position.Position = pos;
                    liveData.SessionResultsPositions[currentSessionNum].Add(position);
                }
                else if (inResultsPositions && liveData.SessionResultsPositions[currentSessionNum].Count > 0)
                {
                    var currentPos = liveData.SessionResultsPositions[currentSessionNum].Last();

                    if (trimmed.StartsWith("ClassPosition:") && TryParseIntValue(trimmed, out int classPos))
                        currentPos.ClassPosition = classPos;
                    else if (trimmed.StartsWith("CarIdx:") && TryParseIntValue(trimmed, out int carIdx))
                        currentPos.CarIdx = carIdx;
                    else if (trimmed.StartsWith("Lap:") && TryParseIntValue(trimmed, out int lap))
                        currentPos.Lap = lap;
                    else if (trimmed.StartsWith("Time:") && TryParseFloatValue(trimmed, out float time))
                        currentPos.Time = time;
                    else if (trimmed.StartsWith("FastestTime:") && TryParseFloatValue(trimmed, out float fastTime))
                        currentPos.FastestTime = fastTime;
                    else if (trimmed.StartsWith("LastTime:") && TryParseFloatValue(trimmed, out float lastTime))
                        currentPos.LastTime = lastTime;
                }
                else if (inSessionInfo && currentSessionIdx == currentSessionNum && trimmed.StartsWith("ResultsFastestLap:"))
                {
                    inResultsFastestLap = true;
                    inResultsPositions = false;
                    liveData.SessionFastestLaps[currentSessionNum].Clear();
                }
                else if (inResultsFastestLap && trimmed.StartsWith("- CarIdx:"))
                {
                    var fastLap = new LiveSessionData.FastestLapResult();
                    if (TryParseIntValue(trimmed, out int carIdx))
                        fastLap.CarIdx = carIdx;
                    liveData.SessionFastestLaps[currentSessionNum].Add(fastLap);
                }
                else if (inResultsFastestLap && liveData.SessionFastestLaps[currentSessionNum].Count > 0)
                {
                    var currentFast = liveData.SessionFastestLaps[currentSessionNum].Last();

                    if (trimmed.StartsWith("FastestLap:") && TryParseIntValue(trimmed, out int fastLap))
                        currentFast.FastestLap = fastLap;
                    else if (trimmed.StartsWith("FastestTime:") && TryParseFloatValue(trimmed, out float fastTime))
                        currentFast.FastestTime = fastTime;
                }
                // Legacy qualify results
                else if (trimmed.StartsWith("QualifyResultsInfo:"))
                {
                    inQualifyResults = true;
                    inSessionInfo = false;
                    inResultsPositions = false;
                    inResultsFastestLap = false;
                }
                else if (inQualifyResults && trimmed.StartsWith("- Position:"))
                {
                    currentQualifyCarIdx = -1;
                }
                else if (inQualifyResults && trimmed.StartsWith("CarIdx:"))
                {
                    if (TryParseIntValue(trimmed, out int carIdx))
                        currentQualifyCarIdx = carIdx;
                }
                else if (inQualifyResults && currentQualifyCarIdx >= 0)
                {
                    if (trimmed.StartsWith("Position:") && TryParseIntValue(trimmed, out int position))
                        liveData.QualifyPositions[currentQualifyCarIdx] = position;
                    else if (trimmed.StartsWith("FastestTime:") && TryParseFloatValue(trimmed, out float fastTime))
                        liveData.QualifyFastestTimes[currentQualifyCarIdx] = fastTime;
                }
            }

            return true;
        }

        private bool TryParseIntValue(string line, out int value)
        {
            var parts = line.Split(':', 2);
            value = 0;
            return parts.Length >= 2 && int.TryParse(parts[1].Trim(), out value);
        }

        private bool TryParseFloatValue(string line, out float value)
        {
            var parts = line.Split(':', 2);
            value = 0f;
            return parts.Length >= 2 && float.TryParse(parts[1].Trim(), out value);
        }
    }
}