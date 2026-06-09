using System.Collections.Generic;
using System.Globalization;
using SVappsLAB.iRacingTelemetrySDK;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Maps the SDK's parsed TelemetrySessionInfo into VISOR's existing
    /// StaticEventData / SessionTransitionData / LiveSessionData shapes.
    /// </summary>
    internal static class SessionDataAdapter
    {
        public static void Apply(
            TelemetrySessionInfo info,
            StaticEventData staticData,
            SessionTransitionData transitionData,
            LiveSessionData liveData)
        {
            if (info == null) return;

            ApplyWeekend(info.WeekendInfo, staticData);
            ApplyDrivers(info.DriverInfo, staticData, transitionData);
            ApplySchedule(info.SessionInfo, staticData);
            ApplyTransition(info.SessionInfo, staticData, transitionData);
            ApplyLive(info, transitionData.CurrentSessionNum, liveData);
        }

        private static void ApplyWeekend(WeekendInfo src, StaticEventData dst)
        {
            if (src == null) return;
            dst.Weekend.TrackName = src.TrackName ?? string.Empty;
            dst.Weekend.TrackConfig = src.TrackConfigName ?? string.Empty;
            dst.Weekend.TrackDisplayName = src.TrackDisplayName ?? string.Empty;
            dst.Weekend.TrackDisplayShortName = src.TrackDisplayShortName ?? string.Empty;
            dst.Weekend.TrackLength = ParseTrackLength(src.TrackLength);
            dst.IncidentLimit = ParseIncidentLimit(src.WeekendOptions?.IncidentLimit);
        }

        private static void ApplyDrivers(DriverInfo src, StaticEventData dst, SessionTransitionData transition)
        {
            // Rebuild from scratch so a driver leaving the session doesn't linger.
            dst.Drivers.Clear();
            if (src?.Drivers == null) return;

            foreach (var d in src.Drivers)
            {
                if (d.CarIdx < 0 || d.CarIdx >= 64) continue;
                dst.Drivers[d.CarIdx] = new StaticEventData.DriverInfo
                {
                    UserName = d.UserName ?? string.Empty,
                    CarNumber = d.CarNumber ?? string.Empty,
                    CarNumberRaw = d.CarNumberRaw,
                    CarClassID = d.CarClassID,
                    CarClassColor = d.CarClassColor,
                    IsAI = d.CarIsAI != 0,
                    CarClassEstLapTime = d.CarClassEstLapTime
                };
                transition.DriverIncidentCounts[d.CarIdx] = d.CurDriverIncidentCount;
            }
        }

        private static void ApplySchedule(SessionInfo src, StaticEventData dst)
        {
            if (src?.Sessions == null) return;
            foreach (var s in src.Sessions)
            {
                if (!dst.Schedule.Sessions.TryGetValue(s.SessionNum, out var def))
                {
                    def = new StaticEventData.SessionSchedule.SessionDefinition { SessionNum = s.SessionNum };
                    dst.Schedule.Sessions[s.SessionNum] = def;
                }
                def.SessionType = s.SessionType ?? string.Empty;
                def.SessionName = s.SessionName ?? string.Empty;
                def.SessionLaps = ParseSessionLaps(s.SessionLaps);
                def.SessionTimeSeconds = ParseSessionTime(s.SessionTime);
            }
        }

        private static void ApplyTransition(SessionInfo src, StaticEventData staticData, SessionTransitionData transition)
        {
            if (src == null) return;
            transition.CurrentSessionNum = src.CurrentSessionNum;
            if (staticData.Schedule.Sessions.TryGetValue(src.CurrentSessionNum, out var def))
            {
                transition.CurrentSessionType = def.SessionType;
                transition.CurrentSessionName = def.SessionName;
            }
        }

        private static void ApplyLive(TelemetrySessionInfo info, int currentSessionNum, LiveSessionData dst)
        {
            EnsureSessionBuckets(dst, currentSessionNum);

            if (info.SessionInfo?.Sessions != null)
            {
                foreach (var s in info.SessionInfo.Sessions)
                {
                    if (s.SessionNum != currentSessionNum) continue;

                    if (s.ResultsPositions != null)
                    {
                        dst.SessionResultsPositions[currentSessionNum].Clear();
                        foreach (var rp in s.ResultsPositions)
                        {
                            dst.SessionResultsPositions[currentSessionNum].Add(new LiveSessionData.ResultPosition
                            {
                                Position = rp.Position,
                                ClassPosition = rp.ClassPosition,
                                CarIdx = rp.CarIdx,
                                Lap = rp.Lap,
                                Time = rp.Time,
                                FastestTime = rp.FastestTime,
                                LastTime = rp.LastTime
                            });
                        }
                    }

                    if (s.ResultsFastestLap != null)
                    {
                        dst.SessionFastestLaps[currentSessionNum].Clear();
                        foreach (var fl in s.ResultsFastestLap)
                        {
                            dst.SessionFastestLaps[currentSessionNum].Add(new LiveSessionData.FastestLapResult
                            {
                                CarIdx = fl.CarIdx,
                                FastestLap = fl.FastestLap,
                                FastestTime = fl.FastestTime
                            });
                        }
                    }
                }
            }

            if (info.QualifyResultsInfo?.Results != null)
            {
                foreach (var r in info.QualifyResultsInfo.Results)
                {
                    if (r.CarIdx < 0) continue;
                    dst.QualifyPositions[r.CarIdx] = r.Position;
                    dst.QualifyFastestTimes[r.CarIdx] = r.FastestTime;
                }
            }
        }

        private static void EnsureSessionBuckets(LiveSessionData dst, int sessionNum)
        {
            if (!dst.SessionResultsPositions.ContainsKey(sessionNum))
                dst.SessionResultsPositions[sessionNum] = new List<LiveSessionData.ResultPosition>();
            if (!dst.SessionFastestLaps.ContainsKey(sessionNum))
                dst.SessionFastestLaps[sessionNum] = new List<LiveSessionData.FastestLapResult>();
        }

        private static int ParseIncidentLimit(string? raw)
        {
            // "unlimited" or any non-numeric value → 0 (treated as no cap by consumers).
            if (string.IsNullOrEmpty(raw)) return 0;
            return int.TryParse(raw.Trim(), out var v) ? v : 0;
        }

        private static float ParseTrackLength(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0f;
            var s = raw.Trim();
            var isKm = s.EndsWith(" km", System.StringComparison.Ordinal);
            var numStr = s.Replace(" km", "").Replace(" m", "");
            // iRacing YAML uses period as decimal separator regardless of system locale.
            if (!float.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0f;
            return isKm ? v * 1000f : v;
        }

        private static int ParseSessionLaps(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return -1;
            var s = raw.Trim();
            if (s == "unlimited") return -1;
            return int.TryParse(s, out var laps) ? laps : -1;
        }

        private static double ParseSessionTime(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0.0;
            var s = raw.Replace(" sec", "").Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
        }
    }
}
