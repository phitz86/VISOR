namespace VISOR.Tests
{
    /// <summary>Session info fixtures shared by the session-info tests.</summary>
    internal static class SessionFixtures
    {
        // Made-up session info in iRacing's layout: unquoted values, one-space indents, lists
        // written "- Key: value" at their parent's indent. No real names or IDs. A raw string
        // takes the source file's line endings, which are CRLF in a Windows checkout, so they're
        // fixed to "\n" here (iRacing's own) so the tests' multi-line edits find their text.
        public static readonly string Basic = """
            ---
            WeekendInfo:
             TrackName: testtrack gp
             TrackID: 999
             TrackLength: 4.21 km
             TrackDisplayName: Test Raceway
             TrackConfigName: Grand Prix
             WeekendOptions:
              NumStarters: 3
            SessionInfo:
             Sessions:
             - SessionNum: 0
               SessionLaps: unlimited
               SessionTime: 1800.0000 sec
               SessionType: Practice
               SessionName: PRACTICE
             - SessionNum: 1
               SessionLaps: 12
               SessionTime: unlimited
               SessionType: Race
               SessionName: RACE
            DriverInfo:
             DriverCarIdx: 1
             Drivers:
             - CarIdx: 0
               UserName: Pace Car
               AbbrevName:
               UserID: -1
               CarNumber: 0
               CarClassID: 11
               CarScreenName: Safety Car
               CarIsPaceCar: 1
             - CarIdx: 1
               UserName: Alex Example
               AbbrevName: Example, A
               UserID: 100001
               CarNumber: 7
               ClubName: Test Club
               CarClassID: 2268
               CarScreenName: Test GT4
               CarIsPaceCar: 0
             - CarIdx: 2
               UserName: Sam Sample
               AbbrevName: Sample, S
               UserID: 100002
               CarNumber: 21
               ClubName: Test Club
               CarClassID: 2268
               CarScreenName: Test GT4
               CarIsPaceCar: 0
            CarSetup:
             UpdateCount: 2
             Tires:
              LeftFront:
               StartingPressure: 172 kPa

            ...
            """.ReplaceLineEndings("\n");

        // A race weekend: practice with results, lone qualifying, a race; qualifying results; the
        // weekend's subsession, incident limit and the player's car. Made-up names, as above.
        public static readonly string Weekend = """
            ---
            WeekendInfo:
             TrackName: testtrack gp
             TrackID: 999
             TrackLength: 4.21 km
             TrackDisplayName: Test Raceway
             TrackDisplayShortName: Test
             TrackConfigName: Grand Prix
             EventType: Race
             SubSessionID: 12345
             WeekendOptions:
              NumStarters: 4
              IncidentLimit: 17
            SessionInfo:
             CurrentSessionNum: 0
             Sessions:
             - SessionNum: 0
               SessionLaps: unlimited
               SessionTime: 1800.0000 sec
               SessionType: Practice
               SessionName: PRACTICE
               ResultsPositions:
               - Position: 1
                 ClassPosition: 0
                 CarIdx: 2
                 Lap: 5
                 Time: 81.5000
                 FastestLap: 5
                 FastestTime: 81.5000
                 LastTime: 82.0000
               - Position: 2
                 ClassPosition: 1
                 CarIdx: 1
                 Lap: 4
                 Time: 82.2500
                 FastestLap: 4
                 FastestTime: 82.2500
                 LastTime: 83.0000
               - Position: 3
                 ClassPosition: 0
                 CarIdx: 3
                 Lap: 0
                 Time: -1.0000
                 FastestLap: 0
                 FastestTime: -1.0000
                 LastTime: -1.0000
               ResultsFastestLap:
               - CarIdx: 2
                 FastestLap: 5
                 FastestTime: 81.5000
             - SessionNum: 1
               SessionLaps: 2
               SessionTime: 480.0000 sec
               SessionType: Lone Qualify
               SessionName: QUALIFY
               ResultsPositions:
               ResultsFastestLap:
             - SessionNum: 2
               SessionLaps: unlimited
               SessionTime: 1800.0000 sec
               SessionType: Race
               SessionName: RACE
               ResultsPositions:
               ResultsFastestLap:
            QualifyResultsInfo:
             Results:
             - Position: 0
               ClassPosition: 0
               CarIdx: 2
               FastestLap: 2
               FastestTime: 80.9000
             - Position: 1
               ClassPosition: 1
               CarIdx: 1
               FastestLap: 2
               FastestTime: 81.1000
            DriverInfo:
             DriverCarIdx: 1
             DriverCarRedLine: 7200.000
             DriverCarSLFirstRPM: 5500.000
             DriverCarSLShiftRPM: 6000.000
             DriverCarSLLastRPM: 6100.000
             DriverCarSLBlinkRPM: 6450.000
             DriverCarGearNumForward: 6
             DriverCarVersion: 2026.09.25.02
             Drivers:
             - CarIdx: 0
               UserName: Pace Car
               CarNumber: 0
               CarNumberRaw: 0
               CarClassID: 11
               CarClassColor: 0xffffff
               CarClassEstLapTime: 90.0000
               CarIsAI: 0
               CarIsPaceCar: 1
               CarPath: safety pcporsche911cup
               CarScreenName: Safety Car
               CurDriverIncidentCount: 0
             - CarIdx: 1
               UserName: Alex Example
               CarNumber: 7
               CarNumberRaw: 7
               CarClassID: 2268
               CarClassColor: 0xffda59
               CarClassEstLapTime: 81.2000
               CarIsAI: 0
               CarIsPaceCar: 0
               CarPath: testgt4
               CarScreenName: Test GT4
               CurDriverIncidentCount: 4
             - CarIdx: 2
               UserName: Sam Sample
               CarNumber: 21
               CarNumberRaw: 21
               CarClassID: 2268
               CarClassColor: 0xffda59
               CarClassEstLapTime: 81.2000
               CarIsAI: 1
               CarIsPaceCar: 0
               CarPath: testgt4
               CarScreenName: Test GT4
               CurDriverIncidentCount: 0
             - CarIdx: 3
               UserName: Jo Placeholder
               CarNumber: 3
               CarNumberRaw: 3
               CarClassID: 4029
               CarClassColor: 0x33ceff
               CarClassEstLapTime: 77.5000
               CarIsAI: 0
               CarIsPaceCar: 0
               CarPath: testgt3
               CarScreenName: Test GT3
               CurDriverIncidentCount: 2

            ...
            """.ReplaceLineEndings("\n");
    }
}
