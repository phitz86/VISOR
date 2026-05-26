using System.Collections.Generic;
using System.Linq;
using VISOR.Diagnostics;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Walks the old-parser-produced state and the SDK-adapter-produced state,
    /// logging [ParserDiff] lines for any field-level divergence.
    /// Dedupe boundary is CurrentSessionNum: when the session changes,
    /// the logged-signature cache is cleared so each session prints a fresh report
    /// of any remaining drift. Signatures are field-path + old + new, so a field
    /// whose value flips logs once for each old/new pair.
    /// </summary>
    internal class ParserDiffComparator
    {
        private readonly HashSet<string> _loggedSignatures = new();
        private int _lastSessionNumForDedupe = int.MinValue;

        public void Compare(
            StaticEventData oldStatic, StaticEventData newStatic,
            SessionTransitionData oldTrans, SessionTransitionData newTrans,
            LiveSessionData oldLive, LiveSessionData newLive)
        {
            // Reset dedupe when CurrentSessionNum (per old path) changes.
            if (oldTrans.CurrentSessionNum != _lastSessionNumForDedupe)
            {
                _loggedSignatures.Clear();
                _lastSessionNumForDedupe = oldTrans.CurrentSessionNum;
            }

            CompareWeekend(oldStatic, newStatic);
            LogIfDifferent("IncidentLimit", oldStatic.IncidentLimit, newStatic.IncidentLimit);
            CompareDrivers(oldStatic.Drivers, newStatic.Drivers);
            CompareSchedule(oldStatic.Schedule.Sessions, newStatic.Schedule.Sessions);
            CompareTransition(oldTrans, newTrans);
            CompareLive(oldLive, newLive);
        }

        private void CompareWeekend(StaticEventData oldS, StaticEventData newS)
        {
            LogIfDifferent("Weekend.TrackName", oldS.Weekend.TrackName, newS.Weekend.TrackName);
            LogIfDifferent("Weekend.TrackConfig", oldS.Weekend.TrackConfig, newS.Weekend.TrackConfig);
            LogIfDifferent("Weekend.TrackDisplayName", oldS.Weekend.TrackDisplayName, newS.Weekend.TrackDisplayName);
            LogIfDifferent("Weekend.TrackDisplayShortName", oldS.Weekend.TrackDisplayShortName, newS.Weekend.TrackDisplayShortName);
            LogIfDifferent("Weekend.TrackLength", oldS.Weekend.TrackLength, newS.Weekend.TrackLength);
        }

        private void CompareDrivers(
            Dictionary<int, StaticEventData.DriverInfo> oldD,
            Dictionary<int, StaticEventData.DriverInfo> newD)
        {
            foreach (var carIdx in oldD.Keys.Union(newD.Keys))
            {
                var oldDriver = oldD.GetValueOrDefault(carIdx);
                var newDriver = newD.GetValueOrDefault(carIdx);
                if (oldDriver == null || newDriver == null)
                {
                    LogIfDifferent($"Driver[{carIdx}].present", oldDriver != null, newDriver != null);
                    continue;
                }
                LogIfDifferent($"Driver[{carIdx}].UserName", oldDriver.UserName, newDriver.UserName);
                LogIfDifferent($"Driver[{carIdx}].CarNumber", oldDriver.CarNumber, newDriver.CarNumber);
                LogIfDifferent($"Driver[{carIdx}].CarNumberRaw", oldDriver.CarNumberRaw, newDriver.CarNumberRaw);
                LogIfDifferent($"Driver[{carIdx}].CarClassID", oldDriver.CarClassID, newDriver.CarClassID);
                LogIfDifferent($"Driver[{carIdx}].CarClassColor", oldDriver.CarClassColor, newDriver.CarClassColor);
                LogIfDifferent($"Driver[{carIdx}].IsAI", oldDriver.IsAI, newDriver.IsAI);
                LogIfDifferent($"Driver[{carIdx}].CarClassEstLapTime", oldDriver.CarClassEstLapTime, newDriver.CarClassEstLapTime);
            }
        }

        private void CompareSchedule(
            Dictionary<int, StaticEventData.SessionSchedule.SessionDefinition> oldSch,
            Dictionary<int, StaticEventData.SessionSchedule.SessionDefinition> newSch)
        {
            foreach (var num in oldSch.Keys.Union(newSch.Keys))
            {
                var oldDef = oldSch.GetValueOrDefault(num);
                var newDef = newSch.GetValueOrDefault(num);
                if (oldDef == null || newDef == null)
                {
                    LogIfDifferent($"Session[{num}].present", oldDef != null, newDef != null);
                    continue;
                }
                LogIfDifferent($"Session[{num}].SessionType", oldDef.SessionType, newDef.SessionType);
                LogIfDifferent($"Session[{num}].SessionName", oldDef.SessionName, newDef.SessionName);
                LogIfDifferent($"Session[{num}].SessionLaps", oldDef.SessionLaps, newDef.SessionLaps);
                LogIfDifferent($"Session[{num}].SessionTimeSeconds", oldDef.SessionTimeSeconds, newDef.SessionTimeSeconds);
            }
        }

        private void CompareTransition(SessionTransitionData oldT, SessionTransitionData newT)
        {
            LogIfDifferent("Transition.CurrentSessionNum", oldT.CurrentSessionNum, newT.CurrentSessionNum);
            LogIfDifferent("Transition.CurrentSessionType", oldT.CurrentSessionType, newT.CurrentSessionType);
            LogIfDifferent("Transition.CurrentSessionName", oldT.CurrentSessionName, newT.CurrentSessionName);

            foreach (var carIdx in oldT.DriverIncidentCounts.Keys.Union(newT.DriverIncidentCounts.Keys))
            {
                int oldVal = oldT.DriverIncidentCounts.GetValueOrDefault(carIdx);
                int newVal = newT.DriverIncidentCounts.GetValueOrDefault(carIdx);
                LogIfDifferent($"Transition.DriverIncidentCounts[{carIdx}]", oldVal, newVal);
            }
        }

        private void CompareLive(LiveSessionData oldL, LiveSessionData newL)
        {
            foreach (var sessionNum in oldL.SessionResultsPositions.Keys.Union(newL.SessionResultsPositions.Keys))
            {
                var oldList = oldL.SessionResultsPositions.GetValueOrDefault(sessionNum, new List<LiveSessionData.ResultPosition>());
                var newList = newL.SessionResultsPositions.GetValueOrDefault(sessionNum, new List<LiveSessionData.ResultPosition>());
                if (oldList.Count != newList.Count)
                {
                    LogIfDifferent($"Live.SessionResultsPositions[{sessionNum}].Count", oldList.Count, newList.Count);
                }
                int n = System.Math.Min(oldList.Count, newList.Count);
                for (int i = 0; i < n; i++)
                {
                    var prefix = $"Live.SessionResultsPositions[{sessionNum}][{i}]";
                    LogIfDifferent($"{prefix}.Position", oldList[i].Position, newList[i].Position);
                    LogIfDifferent($"{prefix}.ClassPosition", oldList[i].ClassPosition, newList[i].ClassPosition);
                    LogIfDifferent($"{prefix}.CarIdx", oldList[i].CarIdx, newList[i].CarIdx);
                    LogIfDifferent($"{prefix}.Lap", oldList[i].Lap, newList[i].Lap);
                    LogIfDifferent($"{prefix}.Time", oldList[i].Time, newList[i].Time);
                    LogIfDifferent($"{prefix}.FastestTime", oldList[i].FastestTime, newList[i].FastestTime);
                    LogIfDifferent($"{prefix}.LastTime", oldList[i].LastTime, newList[i].LastTime);
                }
            }

            foreach (var sessionNum in oldL.SessionFastestLaps.Keys.Union(newL.SessionFastestLaps.Keys))
            {
                var oldList = oldL.SessionFastestLaps.GetValueOrDefault(sessionNum, new List<LiveSessionData.FastestLapResult>());
                var newList = newL.SessionFastestLaps.GetValueOrDefault(sessionNum, new List<LiveSessionData.FastestLapResult>());
                if (oldList.Count != newList.Count)
                {
                    LogIfDifferent($"Live.SessionFastestLaps[{sessionNum}].Count", oldList.Count, newList.Count);
                }
                int n = System.Math.Min(oldList.Count, newList.Count);
                for (int i = 0; i < n; i++)
                {
                    var prefix = $"Live.SessionFastestLaps[{sessionNum}][{i}]";
                    LogIfDifferent($"{prefix}.CarIdx", oldList[i].CarIdx, newList[i].CarIdx);
                    LogIfDifferent($"{prefix}.FastestLap", oldList[i].FastestLap, newList[i].FastestLap);
                    LogIfDifferent($"{prefix}.FastestTime", oldList[i].FastestTime, newList[i].FastestTime);
                }
            }

            foreach (var carIdx in oldL.QualifyPositions.Keys.Union(newL.QualifyPositions.Keys))
            {
                int oldVal = oldL.QualifyPositions.GetValueOrDefault(carIdx);
                int newVal = newL.QualifyPositions.GetValueOrDefault(carIdx);
                LogIfDifferent($"Live.QualifyPositions[{carIdx}]", oldVal, newVal);
            }

            foreach (var carIdx in oldL.QualifyFastestTimes.Keys.Union(newL.QualifyFastestTimes.Keys))
            {
                float oldVal = oldL.QualifyFastestTimes.GetValueOrDefault(carIdx);
                float newVal = newL.QualifyFastestTimes.GetValueOrDefault(carIdx);
                LogIfDifferent($"Live.QualifyFastestTimes[{carIdx}]", oldVal, newVal);
            }
        }

        private void LogIfDifferent(string field, object oldValue, object newValue)
        {
            if (Equals(oldValue, newValue)) return;
            var oldStr = oldValue?.ToString() ?? "<null>";
            var newStr = newValue?.ToString() ?? "<null>";
            var sig = $"{field}|{oldStr}|{newStr}";
            if (_loggedSignatures.Add(sig))
            {
                Log.Warning($"[ParserDiff] {field}: old={oldStr} new={newStr}");
            }
        }

        public void Reset()
        {
            _loggedSignatures.Clear();
            _lastSessionNumForDedupe = int.MinValue;
        }
    }
}
