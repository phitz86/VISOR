using VISOR.Telemetry;
using Xunit;

namespace VISOR.Tests
{
    /// <summary>
    /// Characterization tests: they record what PositionCalculator does today, so the split
    /// planned for it (FinishTracker / CarTrackingCache / RunningOrder) can be checked against
    /// them unchanged. Frames are counted, not timed: at 60 Hz, 180 frames is 3 seconds.
    /// </summary>
    public class PositionCalculatorTests
    {
        // --- Guards and the roster ---

        [Fact]
        public void DoesNothing_UntilSessionDataIsReady()
        {
            var race = new Race();
            race.Car(1).Place(1, 1, 0.5f);
            race.Session.IsDataReady = false;

            race.Frame(5);

            Assert.Empty(race.Calc.ValidCarIndices);
            Assert.Equal(-1, race.ClassPos(1));
        }

        [Fact]
        public void Roster_TakesACar_OnItsSecondValidFrame()
        {
            var race = new Race();
            race.Car(1).Place(1, 1, 0.5f);

            race.Frame();
            Assert.Empty(race.Calc.ValidCarIndices);
            Assert.Equal(-1, race.ClassPos(1));

            race.Frame();
            Assert.Contains(1, race.Calc.ValidCarIndices);
            Assert.Equal(1, race.ClassPos(1));
            Assert.Equal(1, race.Overall(1));
        }

        [Fact]
        public void Roster_NeedsANameAndANumber()
        {
            var race = new Race();
            race.Car(1).Place(1, 1, 0.5f);
            race.Session.UserNames[1] = string.Empty;

            race.Frame(3);

            Assert.Empty(race.Calc.ValidCarIndices);
        }

        [Fact]
        public void Roster_DropsACar_ThreeSecondsAfterItsTelemetryStops()
        {
            var race = new Race();
            race.Car(1).Place(1, 1, 0.5f);
            race.Frame(2);

            race.LoseTelemetry(1);
            race.Frame(180);
            Assert.Contains(1, race.Calc.ValidCarIndices);
            Assert.Equal(0.5f, race.Calc.GetEffectiveLapDistPct(1));   // stopped car: holds its spot

            race.Frame();   // 181st: the cache has expired, the roster catches up a frame later
            Assert.Equal(-1f, race.Calc.GetEffectiveLapDistPct(1));
            Assert.Contains(1, race.Calc.ValidCarIndices);

            race.Frame();
            Assert.DoesNotContain(1, race.Calc.ValidCarIndices);
        }

        // --- Prediction through short telemetry gaps ---

        [Fact]
        public void Prediction_KeepsAMovingCarGoing_ThroughAGap()
        {
            var race = new Race();
            race.Car(1).Car(2);
            race.Place(1, 3, 0.40f);
            race.Place(2, 3, 0.39f);
            race.Drive(30, 0.001f, 1, 2);

            race.LoseTelemetry(1);
            race.Drive(30, 0.001f, 2);

            float predicted = race.Calc.GetEffectiveLapDistPct(1);
            Assert.InRange(predicted, 0.45f, 0.462f);   // ~0.43 + 30 frames at ~0.001
            Assert.Equal(1, race.ClassPos(1));
            Assert.Equal(2, race.ClassPos(2));
        }

        [Fact]
        public void Prediction_CarriesACarAcrossTheLine_WithoutLosingALap()
        {
            var race = new Race();
            race.Car(1).Car(2);
            race.Place(1, 5, 0.95f);
            race.Place(2, 5, 0.90f);
            race.Drive(20, 0.001f, 1, 2);

            // Telemetry for the leader stops just short of the line; the prediction takes it over.
            race.LoseTelemetry(1);
            race.Drive(60, 0.001f, 2);

            Assert.True(race.Calc.GetEffectiveLapDistPct(1) < 0.1f);   // wrapped past S/F...
            Assert.Equal(1, race.ClassPos(1));                            // ...on lap 6: 6.03 vs 5.98, not 5.03
            Assert.Equal(2, race.ClassPos(2));
        }

        [Fact]
        public void Prediction_HoldsACarInThePitLane_WhereItLastWas()
        {
            var race = new Race();
            race.Car(1).Place(1, 3, 0.20f);
            race.Drive(10, 0.001f, 1);
            race.OnPitRoad[1] = true;
            race.Drive(5, 0.001f, 1);
            float last = race.Pct[1];

            race.LoseTelemetry(1);
            race.Frame(30);

            Assert.Equal(last, race.Calc.GetEffectiveLapDistPct(1));
        }

        // --- Lap counter and LapDistPct disagreeing at the line ---

        private static Race TwoCarsNearTheLine()
        {
            var race = new Race();
            race.Car(1).Car(2);
            race.Place(1, 5, 0.985f);   // 1 leads...
            race.Place(2, 5, 0.97f);    // ...2 is just behind, both about to cross
            race.Frame(3);
            return race;
        }

        [Fact]
        public void LapDesync_PctWrapsBeforeTheLapCounter_LeaderStaysP1()
        {
            var race = TwoCarsNearTheLine();
            race.Pct[1] = 0.005f;   // wrapped, CarIdxLap still 5: raw 5.005 would fall behind 5.97
            race.Frame();
            Assert.Equal(1, race.ClassPos(1));

            race.Place(1, 6, 0.006f);   // the counter catches up
            race.Frame();
            Assert.Equal(1, race.ClassPos(1));
        }

        [Fact]
        public void LapDesync_LapCounterTicksBeforePctWraps_CarBehindDoesNotJumpAhead()
        {
            var race = new Race();
            race.Car(1).Car(2);
            race.Place(1, 6, 0.05f);    // 1 has crossed and leads
            race.Place(2, 5, 0.98f);    // 2 is about to cross
            race.Frame(3);

            race.Lap[2] = 6;            // counter first, LapDistPct still 0.99: raw 6.99 would lead
            race.Pct[2] = 0.99f;
            race.Frame();
            Assert.Equal(2, race.ClassPos(2));

            race.Pct[2] = 0.004f;       // then it wraps
            race.Frame();
            Assert.Equal(2, race.ClassPos(2));
        }

        [Fact]
        public void LapDesync_StuckLapCounter_IsBridgedOnlyNearTheLine()
        {
            var race = TwoCarsNearTheLine();
            race.Pct[1] = 0.005f;
            race.Frame();

            // The counter never ticks. Within sight of the line the car keeps its lap...
            for (int i = 0; i < 40; i++)
            {
                race.Pct[1] += 0.001f;
                race.Pct[2] += 0.0001f;
                race.Frame();
            }
            Assert.Equal(1, race.ClassPos(1));

            // ...past it, the raw (stuck) lap number wins.
            race.Pct[1] = 0.12f;
            race.Frame();
            Assert.Equal(2, race.ClassPos(1));
        }

        [Fact]
        public void LapDesync_IsNotAssumedAcrossATelemetryGap()
        {
            var race = TwoCarsNearTheLine();
            race.LoseTelemetry(1);
            race.Frame(5);

            // Back past the line with the counter unchanged: across a gap that can't be told
            // apart from a real lap, so no correction is applied.
            race.Pct[1] = 0.01f;
            race.LapCompleted[1] = 4;
            race.Frame();

            Assert.Equal(2, race.ClassPos(1));
            Assert.Equal(1, race.ClassPos(2));
        }

        // --- Grid order before the green flag ---

        private static Race GridOfThree()
        {
            var race = new Race { SessionState = SessionStates.ParadeLaps };
            race.Car(1, classId: 1).Car(2, classId: 1).Car(3, classId: 2);
            // On the grid LapDistPct straddles the line, so it says nothing about order.
            race.Place(1, 0, 0.99f);
            race.Place(2, 0, 0.01f);
            race.Place(3, 0, 0.98f);
            return race;
        }

        [Fact]
        public void PreGreen_UsesTheLivePositionArrays_WhenTheyCoverEveryCar()
        {
            var race = GridOfThree();
            race.ClassPosition[1] = 2; race.ClassPosition[2] = 1; race.ClassPosition[3] = 1;
            race.Position[1] = 2; race.Position[2] = 1; race.Position[3] = 3;

            race.Frame(2);

            Assert.Equal((2, 1, 1), (race.ClassPos(1), race.ClassPos(2), race.ClassPos(3)));
            Assert.Equal((2, 1, 3), (race.Overall(1), race.Overall(2), race.Overall(3)));
        }

        [Fact]
        public void PreGreen_UsesQualifying_WhenItCoversMoreCarsThanTheLiveArrays()
        {
            var race = GridOfThree();
            race.ClassPosition[2] = 1; race.Position[2] = 1;   // live arrays know one car
            race.Session.QualifyPositions[1] = 1;               // qualifying knows all three
            race.Session.QualifyPositions[2] = 3;
            race.Session.QualifyPositions[3] = 2;

            race.Frame(2);

            Assert.Equal((1, 2, 1), (race.ClassPos(1), race.ClassPos(2), race.ClassPos(3)));
            Assert.Equal((1, 3, 2), (race.Overall(1), race.Overall(2), race.Overall(3)));
        }

        [Fact]
        public void PreGreen_TieGoesToTheLiveArrays_AndCarsWithNoGridSortByIndex()
        {
            var race = GridOfThree();
            race.ClassPosition[2] = 1; race.Position[2] = 1;   // live: one car
            race.Session.QualifyPositions[1] = 1;               // qualifying: one car

            race.Frame(2);

            Assert.Equal((2, 1, 3), (race.Overall(1), race.Overall(2), race.Overall(3)));
            Assert.Equal((2, 1), (race.ClassPos(1), race.ClassPos(2)));
        }

        [Fact]
        public void GreenLatch_ACarThatHasTakenTheGreen_RanksAheadOfTheGrid()
        {
            var race = GridOfThree();
            race.ClassPosition[1] = 1; race.ClassPosition[2] = 2; race.ClassPosition[3] = 1;
            race.Position[1] = 1; race.Position[2] = 2; race.Position[3] = 3;
            race.Frame(2);
            Assert.Equal(1, race.ClassPos(1));

            race.SessionState = SessionStates.Racing;
            race.Place(2, 1, 0.01f);    // car 2 crosses the line under green: LapCompleted 0
            race.Frame(2);

            Assert.Equal(1, race.ClassPos(2));
            Assert.Equal(2, race.ClassPos(1));
            Assert.Equal(1, race.Overall(2));
        }

        [Fact]
        public void GreenLatch_CrossingTheLineOnTheParadeLap_DoesNotCount()
        {
            var race = GridOfThree();
            race.ClassPosition[1] = 1; race.ClassPosition[2] = 2; race.ClassPosition[3] = 1;
            race.Position[1] = 1; race.Position[2] = 2; race.Position[3] = 3;
            race.Frame(2);

            race.Place(2, 1, 0.01f);    // LapCompleted 0, but the session isn't green yet
            race.Frame(2);

            Assert.Equal(1, race.ClassPos(1));
            Assert.Equal(2, race.ClassPos(2));
        }

        // --- Freezing finishing positions under the checkered ---

        [Fact]
        public void Finish_TheLeaderFreezes_OnTheFrameTheCheckeredComesOut()
        {
            var race = new Race();
            race.Car(1).Car(2);
            race.Place(1, 10, 0.99f);
            race.Place(2, 10, 0.95f);
            race.Frame(2);

            // iRacing flips the session to Checkered because the leader crossed: same frame.
            race.SessionState = SessionStates.Checkered;
            race.Cross(1);
            race.Frame();

            // The winner cools down; car 2 takes the flag and passes it on the slowing-down lap.
            for (int i = 0; i < 60; i++)
            {
                race.Pct[1] += 0.0005f;
                race.Drive(1, 0.003f, 2);
            }
            Assert.True(race.Pct[2] > race.Pct[1] && race.Lap[2] == race.Lap[1]);

            // Both hold where they finished. Had the leader's crossing been missed (the bug the
            // baseline comment in FreezeFinishingPositions describes), nothing would freeze and
            // car 2 would now lead.
            Assert.Equal((1, 2), (race.ClassPos(1), race.ClassPos(2)));
            Assert.Equal((1, 2), (race.Overall(1), race.Overall(2)));
        }

        [Fact]
        public void Finish_CarsCrossingBeforeTheOverallLeader_KeepRacing()
        {
            var race = new Race();
            race.Car(1, classId: 1).Car(2, classId: 2).Car(3, classId: 1);
            race.Place(1, 10, 0.98f);   // overall leader
            race.Place(2, 10, 0.97f);   // leader of the slower class, just behind on track
            race.Place(3, 9, 0.995f);   // lapped, right at the line
            race.Frame(2);

            race.SessionState = SessionStates.Checkered;
            race.Cross(3);              // lapped car first
            race.Frame();
            race.Cross(2);              // slower-class leader second
            race.Frame();

            // Had either been frozen it would hold the place it crossed in (car 3 P3, car 2 P2).
            // Swap them on track: both move, so both are still racing.
            race.Place(2, 10, 0.30f);
            race.Place(3, 10, 0.50f);
            race.Frame();
            Assert.Equal((1, 2, 3), (race.Overall(1), race.Overall(3), race.Overall(2)));
        }

        [Fact]
        public void Finish_AfterTheLeader_CarsFreezeAsTheyCross_AndLiveCarsSkipFrozenSlots()
        {
            var race = new Race();
            race.Car(1).Car(2).Car(3).Car(4);
            race.Place(1, 10, 0.99f);   // P1
            race.Place(2, 10, 0.60f);   // P2
            race.Place(3, 9, 0.98f);    // P3, a lap down
            race.Place(4, 9, 0.50f);    // P4, a lap down
            race.Frame(2);

            race.SessionState = SessionStates.Checkered;
            race.Cross(1);
            race.Frame();
            race.Cross(3);              // the lapped car takes the flag at P3
            race.Frame();

            // Cars 2 and 4 are the only ones still in the live sort, and they take slots 2 and 4
            // around the frozen 1 and 3 rather than 1 and 2.
            Assert.Equal((1, 2, 3, 4), (race.ClassPos(1), race.ClassPos(2), race.ClassPos(3), race.ClassPos(4)));

            // Car 4 takes the flag in P4, then passes the slowing car 2 on its cool-down lap.
            race.Cross(4);
            race.Frame();
            race.Pct[4] = 0.70f;
            race.Frame();
            Assert.Equal((2, 4), (race.ClassPos(2), race.ClassPos(4)));
        }

        [Fact]
        public void Finish_ACarJoiningUnderTheCheckered_FreezesOnItsNextCrossing()
        {
            var race = new Race();
            race.Car(1).Car(2);
            race.Place(1, 10, 0.99f);
            race.Place(2, 10, 0.50f);
            race.Frame(2);
            race.SessionState = SessionStates.Checkered;
            race.Cross(1);
            race.Frame();

            race.Car(3).Place(3, 10, 0.995f);   // joins: its first lap count is only a baseline
            race.Frame(2);
            race.Cross(3);                       // its first observed crossing...
            race.Frame();
            Assert.Equal(2, race.ClassPos(3));   // ...freezes it in the slot it held, behind the winner

            race.Place(3, 10, 0.10f);            // live, it would now be P3 behind car 2
            race.Frame();
            Assert.Equal(2, race.ClassPos(3));
        }

        [Fact]
        public void Finish_ATelemetryGapUnderTheCheckered_IsNotReadAsACrossing()
        {
            var race = ThreeCarsLeaderHome();   // 1 is home; 2 at 10.50 and 3 at 10.30 are racing

            race.LoseTelemetry(2);              // LapCompleted reads -1 for a few frames
            race.Frame(5);
            race.Place(2, 10, 0.51f);           // and comes back on the same lap
            race.Frame();

            race.Place(3, 10, 0.60f);           // car 3 passes it: car 2 was not frozen
            race.Frame();
            Assert.Equal((2, 3), (race.ClassPos(3), race.ClassPos(2)));
        }

        // --- Holding the place of a car that leaves under the checkered ---

        private static Race ThreeCarsLeaderHome()
        {
            var race = new Race();
            race.Car(1).Car(2).Car(3);
            race.Place(1, 10, 0.99f);
            race.Place(2, 10, 0.50f);
            race.Place(3, 10, 0.30f);
            race.Frame(2);
            race.SessionState = SessionStates.Checkered;
            race.Cross(1);
            race.Frame();
            return race;
        }

        [Fact]
        public void Departed_UnderTheCheckered_ACarLeavingTheSessionKeepsItsPlace()
        {
            var race = ThreeCarsLeaderHome();

            race.LeaveSession(2);
            race.Frame();

            Assert.Equal(2, race.ClassPos(2));
            Assert.Equal(2, race.Overall(2));
            Assert.Equal(3, race.ClassPos(3));
        }

        [Fact]
        public void Departed_TheLeaderLeavingBeforeItCrosses_StillOpensTheFinish()
        {
            var race = new Race();
            race.Car(1).Car(2).Car(3);
            race.Place(1, 10, 0.50f);
            race.Place(2, 10, 0.40f);
            race.Place(3, 10, 0.30f);
            race.Frame(2);
            race.SessionState = SessionStates.Checkered;

            race.LeaveSession(1);
            race.Frame();
            Assert.Equal(1, race.Overall(1));

            // With the winner home, the next car to cross freezes: car 3 holds P3 although it
            // is now ahead of car 2 on the live key (11.002 vs 10.40).
            race.Cross(3);
            race.Frame();
            Assert.Equal((2, 3), (race.ClassPos(2), race.ClassPos(3)));
        }

        [Fact]
        public void Departed_BeforeTheCheckered_TheCarBehindMovesUp()
        {
            var race = new Race();
            race.Car(1).Car(2).Car(3);
            race.Place(1, 10, 0.50f);
            race.Place(2, 10, 0.40f);
            race.Place(3, 10, 0.30f);
            race.Frame(2);

            race.LeaveSession(1);
            race.Frame();

            Assert.Equal(1, race.ClassPos(2));
            Assert.Equal(2, race.ClassPos(3));
        }

        [Fact]
        public void Departed_UnderTheCheckered_ACarWhoseTelemetryStopsKeepsItsPlace()
        {
            var race = ThreeCarsLeaderHome();

            // B12: about three seconds in, the car drops out of the running order a frame before
            // it leaves the roster. The car behind must not take its place, even for that frame.
            race.LoseTelemetry(2);
            for (int i = 0; i < 200; i++)
            {
                race.Frame();
                Assert.Equal(3, race.ClassPos(3));
            }

            Assert.DoesNotContain(2, race.Calc.ValidCarIndices);
            Assert.Equal((2, 2), (race.ClassPos(2), race.Overall(2)));
            Assert.Equal(3, race.Overall(3));
        }

        [Fact]
        public void Departed_BeforeTheCheckered_ACarWhoseTelemetryStopsMakesWay()
        {
            var race = new Race();
            race.Car(1).Car(2).Car(3);
            race.Place(1, 10, 0.50f);
            race.Place(2, 10, 0.40f);
            race.Place(3, 10, 0.30f);
            race.Frame(2);

            race.LoseTelemetry(2);
            race.Frame(200);

            Assert.DoesNotContain(2, race.Calc.ValidCarIndices);
            Assert.Equal((2, 2), (race.ClassPos(3), race.Overall(3)));
        }

        // --- Pace car, practice and qualifying, resets ---

        [Fact]
        public void PaceCar_HasNoOverallPosition_AndDoesNotPushTheFieldDown()
        {
            var race = new Race();
            race.Car(0, classId: IRacingIds.PaceCarClassId).Car(1).Car(2);
            race.Place(0, 3, 0.60f);    // ahead of the field on track
            race.Place(1, 3, 0.50f);
            race.Place(2, 3, 0.40f);

            race.Frame(2);

            Assert.Equal(-1, race.Overall(0));
            Assert.Equal((1, 2), (race.Overall(1), race.Overall(2)));
            Assert.Equal(1, race.ClassPos(0));   // alone in its own class
            Assert.Equal((1, 2), (race.ClassPos(1), race.ClassPos(2)));
        }

        [Fact]
        public void FastestLapMode_LeavesPositionsToTheSessionData_ButKeepsTrackingCars()
        {
            var race = new Race();
            race.Car(1).Place(1, 3, 0.50f);
            race.Session.FastestLapMode = true;

            race.Frame(3);

            Assert.Equal(-1, race.ClassPos(1));
            Assert.Equal(-1, race.Overall(1));
            Assert.Contains(1, race.Calc.ValidCarIndices);
            Assert.Equal(0.5f, race.Calc.GetEffectiveLapDistPct(1));
        }

        [Fact]
        public void SessionChange_ClearsFinishingPositions_ButKeepsTheRoster()
        {
            var race = ThreeCarsLeaderHome();
            Assert.Equal(1, race.ClassPos(1));

            race.SessionNum = 2;
            race.SessionState = SessionStates.Racing;
            race.Place(1, 1, 0.10f);    // new session: car 1 now runs last
            race.Place(2, 1, 0.30f);
            race.Place(3, 1, 0.20f);
            race.Frame();

            Assert.Equal(3, race.ClassPos(1));
            Assert.Equal(1, race.ClassPos(2));
            Assert.Equal(3, race.Calc.ValidCarIndices.Count);
        }

        [Fact]
        public void Reset_ForgetsEverything()
        {
            var race = ThreeCarsLeaderHome();

            race.Calc.Reset();

            Assert.Empty(race.Calc.ValidCarIndices);
            Assert.Equal(-1, race.ClassPos(1));
            Assert.Equal(-1, race.Overall(2));
            Assert.Equal(int.MaxValue, race.Calc.GetFramesSinceValidData(1));
        }
    }
}
