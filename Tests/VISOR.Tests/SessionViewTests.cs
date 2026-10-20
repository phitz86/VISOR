using SVappsLAB.iRacingTelemetrySDK;
using VISOR.Telemetry;
using Xunit;

namespace VISOR.Tests
{
    public class SessionViewTests
    {
        private static TelemetrySessionInfo Parse(string yaml)
        {
            var info = SessionInfoYaml.Parse<TelemetrySessionInfo>(yaml, out string outcome);
            Assert.True(info != null, $"fixture not read: {outcome}");
            return info!;
        }

        private static SessionView Build(string yaml) => SessionViewBuilder.Build(Parse(yaml));

        private static string WithCurrentSession(int sessionNum) =>
            SessionFixtures.Weekend.Replace(" CurrentSessionNum: 0", $" CurrentSessionNum: {sessionNum}");

        // --- What one update holds ---

        [Fact]
        public void Drivers_LandInTheirCarSlots()
        {
            var view = Build(SessionFixtures.Weekend);

            Assert.True(view.IsDataReady);
            Assert.Equal(4, view.DriverCount);
            Assert.Equal("Alex Example", view.UserNames[1]);
            Assert.Equal("21", view.CarNumbers[2]);
            Assert.Equal(IRacingIds.PaceCarClassId, view.CarClassIDs[0]);
            Assert.Equal((2268, 4029), (view.CarClassIDs[1], view.CarClassIDs[3]));
            Assert.Equal(0xffda59, view.CarClassColors[1]);
            Assert.True(view.CarIsAI[2]);
            Assert.False(view.CarIsAI[1]);
            Assert.Equal(81.2f, view.CarClassEstLapTimes[1]);
            Assert.Equal((4, 2), (view.CurDriverIncidentCount[1], view.CurDriverIncidentCount[3]));

            // A slot with no driver: no name or number, zeros elsewhere. Every array covers all slots.
            Assert.Null(view.UserNames[4]);
            Assert.Null(view.CarNumbers[4]);
            Assert.Equal(0, view.CarClassIDs[4]);
            Assert.Equal(SessionView.CarCount, view.UserNames.Length);
            Assert.Equal(SessionView.CarCount, view.CurDriverIncidentCount.Length);
        }

        [Fact]
        public void Track_AndWeekend()
        {
            var view = Build(SessionFixtures.Weekend);

            Assert.Equal(new TrackInfo("testtrack gp", "Grand Prix", "Test Raceway", "Test", 4210f, "Race"), view.Track);
            Assert.Equal("12345", view.SubSessionId);
            Assert.Equal(17, view.IncidentLimit);
        }

        [Fact]
        public void IncidentLimit_Unlimited_IsZero()
        {
            var view = Build(SessionFixtures.Weekend.Replace("IncidentLimit: 17", "IncidentLimit: unlimited"));

            Assert.Equal(0, view.IncidentLimit);
        }

        [Fact]
        public void PlayerCar_IsTheDriverCarIdxEntry()
        {
            var car = Build(SessionFixtures.Weekend).PlayerCar;

            Assert.NotNull(car);
            Assert.Equal(("testgt4", "Test GT4", "2026.09.25.02"), (car!.CarPath, car.CarScreenName, car.CarVersion));
            Assert.Equal((7200f, 5500f, 6000f, 6100f, 6450f), (car.RedLine, car.SLFirstRPM, car.SLShiftRPM, car.SLLastRPM, car.SLBlinkRPM));
            Assert.Equal(6, car.GearNumForward);
        }

        [Fact]
        public void Schedule_LapsTimeAndQualifying()
        {
            var view = Build(SessionFixtures.Weekend);

            Assert.Equal(new[] { 0, 1, 2 }, view.Sessions.Keys);
            Assert.Equal((-1, 2, -1), (view.GetSessionLaps(0), view.GetSessionLaps(1), view.GetSessionLaps(2)));
            Assert.Equal(480.0, view.GetSessionTimeSeconds(1));
            Assert.True(view.IsQualifyingSession(1));
            Assert.False(view.IsQualifyingSession(2));

            // Not in the schedule.
            Assert.Equal(-1, view.GetSessionLaps(9));
            Assert.Equal(0.0, view.GetSessionTimeSeconds(9));
            Assert.False(view.IsQualifyingSession(9));
        }

        [Theory]
        [InlineData("Practice", true, false, false, false)]
        [InlineData("Open Qualify", false, true, false, false)]
        [InlineData("Lone Qualify", false, true, true, false)]
        [InlineData("Race", false, false, false, true)]
        [InlineData("Heat Race", false, false, false, true)]
        [InlineData("Warmup", false, false, false, false)]
        [InlineData("", false, false, false, false)]
        public void SessionTypes_AreClassifiedByTheSameMatchingAsBefore(string type, bool practice, bool qualifying, bool lone, bool race)
        {
            var session = new SessionDefinition(0, type, "", -1, 0, Array.Empty<ResultPosition>(), Array.Empty<FastestLapResult>());

            Assert.Equal((practice, qualifying, lone, race),
                (session.IsPractice, session.IsQualifying, session.IsLoneQualifying, session.IsRace));
        }

        // --- What the current session decides ---

        [Fact]
        public void Practice_OrdersByFastestLap_FromItsOwnResults()
        {
            var view = Build(SessionFixtures.Weekend);

            Assert.Equal("Practice", view.CurrentSession!.SessionType);
            Assert.True(view.ShouldUseFastestLapPositioning());
            Assert.False(view.IsLoneQualifying);

            // Car 3 has no timed lap and is left out. Class position 1-based, overall as given.
            Assert.Equal(new[] { (2, 81.5f, 1, 1), (1, 82.25f, 2, 2) }, view.FastestLapPositions);
        }

        [Fact]
        public void LoneQualifying_HidesTheRelative_AndOrdersByFastestLap()
        {
            var view = Build(WithCurrentSession(1));

            Assert.True(view.IsLoneQualifying);
            Assert.True(view.ShouldUseFastestLapPositioning());
            Assert.Empty(view.FastestLapPositions);   // no laps yet
        }

        [Fact]
        public void Race_OrdersByPositionOnTrack()
        {
            var view = Build(WithCurrentSession(2));

            Assert.False(view.ShouldUseFastestLapPositioning());
            Assert.False(view.IsLoneQualifying);
            Assert.Empty(view.FastestLapPositions);
        }

        [Fact]
        public void ACurrentSessionMissingFromTheSchedule_UsesRacePositioning()
        {
            var view = Build(WithCurrentSession(7));

            Assert.Null(view.CurrentSession);
            Assert.False(view.ShouldUseFastestLapPositioning());
            Assert.False(view.IsLoneQualifying);
        }

        [Fact]
        public void QualifyingResults_AreOneBased_WithZeroForNoResult()
        {
            var view = Build(SessionFixtures.Weekend);

            Assert.Equal((1, 2, 0), (view.QualifyPositions[2], view.QualifyPositions[1], view.QualifyPositions[3]));
            Assert.Equal(80.9f, view.QualifyFastestTimes[2]);
            Assert.Same(view.QualifyPositions, view.GetQualifyResultsPositions());
        }

        [Fact]
        public void Empty_HasNoSession()
        {
            var view = SessionView.Empty;

            Assert.False(view.IsDataReady);
            Assert.Equal(-1, view.CurrentSessionNum);
            Assert.Same(TrackInfo.None, view.Track);
            Assert.Empty(view.Sessions);
            Assert.Null(view.PlayerCar);
            Assert.False(view.ShouldUseFastestLapPositioning());
            Assert.Equal(SessionView.CarCount, view.CarNumbers.Length);
        }
    }

    public class SessionDataCoordinatorTests
    {
        private static TelemetrySessionInfo Parse(string yaml) =>
            SessionInfoYaml.Parse<TelemetrySessionInfo>(yaml, out _)!;

        [Fact]
        public void EachUpdate_PublishesANewView_AndAViewAlreadyTakenDoesNotChange()
        {
            var coordinator = new SessionDataCoordinator();
            Assert.Same(SessionView.Empty, coordinator.Current);

            Assert.True(coordinator.ApplySdkSession(Parse(SessionFixtures.Weekend)));
            var taken = coordinator.Current;   // what a frame in progress holds

            Assert.True(coordinator.ApplySdkSession(Parse(SessionFixtures.Weekend.Replace("UserName: Alex Example", "UserName: Alex Renamed"))));

            Assert.NotSame(taken, coordinator.Current);
            Assert.Equal("Alex Example", taken.UserNames[1]);
            Assert.Equal("Alex Renamed", coordinator.Current.UserNames[1]);
        }

        [Fact]
        public void AnUpdateWithNoDrivers_IsNotPublished_AndThePreviousViewStaysWhole()
        {
            var coordinator = new SessionDataCoordinator();
            coordinator.ApplySdkSession(Parse(SessionFixtures.Weekend));
            var before = coordinator.Current;

            string noDrivers = SessionFixtures.Weekend[..SessionFixtures.Weekend.IndexOf("DriverInfo:", StringComparison.Ordinal)];
            Assert.False(coordinator.ApplySdkSession(Parse(noDrivers)));

            Assert.Same(before, coordinator.Current);
        }

        [Fact]
        public void TheScheduleAndQualifyingResults_AreRebuiltOnEachUpdate()
        {
            var coordinator = new SessionDataCoordinator();
            coordinator.ApplySdkSession(Parse(SessionFixtures.Weekend));
            Assert.Equal(3, coordinator.Current.Sessions.Count);

            // A new subsession with one session and no qualifying yet: nothing from the last one lingers.
            coordinator.ApplySdkSession(Parse(SessionFixtures.Basic));

            Assert.Equal(new[] { 0, 1 }, coordinator.Current.Sessions.Keys);
            Assert.Equal("Race", coordinator.Current.Sessions[1].SessionType);
            Assert.All(coordinator.Current.QualifyPositions, p => Assert.Equal(0, p));
        }

        [Fact]
        public void ClearCache_PublishesEmpty()
        {
            var coordinator = new SessionDataCoordinator();
            coordinator.ApplySdkSession(Parse(SessionFixtures.Weekend));

            coordinator.ClearCache();

            Assert.Same(SessionView.Empty, coordinator.Current);
            Assert.False(coordinator.IsDataReady);
        }
    }
}
