using System.Globalization;
using SVappsLAB.iRacingTelemetrySDK;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Builds a <see cref="SessionView"/> from one parse of the SDK's TelemetrySessionInfo. Every
    /// part is built afresh from that parse, so nothing from an earlier update (a driver who
    /// left, a previous subsession's schedule or qualifying results) carries over.
    /// </summary>
    internal static class SessionViewBuilder
    {
        public static SessionView Build(TelemetrySessionInfo info)
        {
            var userNames = new string[SessionView.CarCount];
            var carNumbers = new string[SessionView.CarCount];
            var carNumberRaw = new int[SessionView.CarCount];
            var carClassIDs = new int[SessionView.CarCount];
            var carClassColors = new int[SessionView.CarCount];
            var carIsAI = new bool[SessionView.CarCount];
            var carClassEstLapTimes = new float[SessionView.CarCount];
            var incidentCounts = new int[SessionView.CarCount];
            int driverCount = 0;

            if (info.DriverInfo?.Drivers != null)
            {
                foreach (var d in info.DriverInfo.Drivers)
                {
                    if (d.CarIdx < 0 || d.CarIdx >= SessionView.CarCount) continue;
                    userNames[d.CarIdx] = d.UserName ?? string.Empty;
                    carNumbers[d.CarIdx] = d.CarNumber ?? string.Empty;
                    carNumberRaw[d.CarIdx] = d.CarNumberRaw;
                    carClassIDs[d.CarIdx] = d.CarClassID;
                    carClassColors[d.CarIdx] = d.CarClassColor;
                    carIsAI[d.CarIdx] = d.CarIsAI != 0;
                    carClassEstLapTimes[d.CarIdx] = d.CarClassEstLapTime;
                    incidentCounts[d.CarIdx] = d.CurDriverIncidentCount;
                    driverCount++;
                }
            }

            var sessions = BuildSchedule(info.SessionInfo);
            var (qualifyPositions, qualifyFastestTimes) = BuildQualifyResults(info.QualifyResultsInfo);
            var weekend = info.WeekendInfo;

            return new SessionView(
                isDataReady: driverCount > 0 && sessions.Count > 0,
                driverCount: driverCount,
                userNames: userNames,
                carNumbers: carNumbers,
                carNumberRaw: carNumberRaw,
                carClassIDs: carClassIDs,
                carClassColors: carClassColors,
                carIsAI: carIsAI,
                carClassEstLapTimes: carClassEstLapTimes,
                curDriverIncidentCount: incidentCounts,
                incidentLimit: ParseIncidentLimit(weekend?.WeekendOptions?.IncidentLimit),
                // SubSessionID is a bare integer in the YAML; Convert.ToString keeps this resilient to the
                // SDK's exact numeric type (int/long/string/nullable) and normalizes it to a comparable key.
                subSessionId: weekend == null ? string.Empty
                    : Convert.ToString(weekend.SubSessionID, CultureInfo.InvariantCulture) ?? string.Empty,
                playerCar: BuildPlayerCar(info.DriverInfo),
                track: BuildTrack(weekend),
                sessions: sessions,
                currentSessionNum: info.SessionInfo?.CurrentSessionNum ?? -1,
                qualifyPositions: qualifyPositions,
                qualifyFastestTimes: qualifyFastestTimes);
        }

        private static TrackInfo BuildTrack(WeekendInfo? src)
        {
            if (src == null) return TrackInfo.None;
            return new TrackInfo(
                Name: src.TrackName ?? string.Empty,
                Config: src.TrackConfigName ?? string.Empty,
                DisplayName: src.TrackDisplayName ?? string.Empty,
                DisplayShortName: src.TrackDisplayShortName ?? string.Empty,
                LengthMeters: ParseTrackLength(src.TrackLength),
                EventType: src.EventType ?? string.Empty);
        }

        private static PlayerCarInfo? BuildPlayerCar(DriverInfo? src)
        {
            var player = src?.Drivers?.FirstOrDefault(d => d.CarIdx == src.DriverCarIdx);
            if (player == null) return null;

            // Convert.* keeps this resilient to the SDK's exact numeric types for these fields.
            var ci = CultureInfo.InvariantCulture;
            return new PlayerCarInfo(
                CarPath: Convert.ToString(player.CarPath, ci) ?? string.Empty,
                CarScreenName: Convert.ToString(player.CarScreenName, ci) ?? string.Empty,
                CarVersion: Convert.ToString(src!.DriverCarVersion, ci) ?? string.Empty,
                RedLine: Convert.ToSingle(src.DriverCarRedLine, ci),
                SLFirstRPM: Convert.ToSingle(src.DriverCarSLFirstRPM, ci),
                SLShiftRPM: Convert.ToSingle(src.DriverCarSLShiftRPM, ci),
                GearNumForward: Convert.ToInt32(src.DriverCarGearNumForward, ci),
                SLLastRPM: Convert.ToSingle(src.DriverCarSLLastRPM, ci),
                SLBlinkRPM: Convert.ToSingle(src.DriverCarSLBlinkRPM, ci));
        }

        private static Dictionary<int, SessionDefinition> BuildSchedule(SessionInfo? src)
        {
            var sessions = new Dictionary<int, SessionDefinition>();
            if (src?.Sessions == null) return sessions;

            foreach (var s in src.Sessions)
            {
                var results = s.ResultsPositions?
                    .Select(rp => new ResultPosition(rp.Position, rp.ClassPosition, rp.CarIdx, rp.Lap,
                        rp.Time, rp.FastestTime, rp.LastTime))
                    .ToArray() ?? Array.Empty<ResultPosition>();
                var fastestLaps = s.ResultsFastestLap?
                    .Select(fl => new FastestLapResult(fl.CarIdx, fl.FastestLap, fl.FastestTime))
                    .ToArray() ?? Array.Empty<FastestLapResult>();

                sessions[s.SessionNum] = new SessionDefinition(
                    s.SessionNum,
                    s.SessionType ?? string.Empty,
                    s.SessionName ?? string.Empty,
                    ParseSessionLaps(s.SessionLaps),
                    ParseSessionTime(s.SessionTime),
                    results,
                    fastestLaps);
            }
            return sessions;
        }

        private static (int[] positions, float[] fastestTimes) BuildQualifyResults(QualifyResultsInfo? src)
        {
            var positions = new int[SessionView.CarCount];
            var fastestTimes = new float[SessionView.CarCount];
            if (src?.Results == null) return (positions, fastestTimes);

            foreach (var r in src.Results)
            {
                if (r.CarIdx < 0 || r.CarIdx >= SessionView.CarCount) continue;
                // QualifyResultsInfo.Position is 0-based in the YAML: the pole-sitter is
                // Position 0. Consumers use 0 as the "no grid slot for this car" sentinel
                // (the backing array defaults to 0), so shift to 1-based on the way in or the
                // pole car reads as unknown and gets sorted to the back of the grid. The value
                // is only ever used as an ordering key, so the shift is safe even if a future
                // build of the sim reports these 1-based already.
                if (r.Position >= 0)
                    positions[r.CarIdx] = r.Position + 1;
                fastestTimes[r.CarIdx] = r.FastestTime;
            }
            return (positions, fastestTimes);
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
            var isKm = s.EndsWith(" km", StringComparison.Ordinal);
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
