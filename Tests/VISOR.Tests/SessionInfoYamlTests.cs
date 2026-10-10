using System.IO;
using System.Linq;
using System.Text.Json;
using SVappsLAB.iRacingTelemetrySDK;
using VISOR.Telemetry;
using Xunit;
using YamlDotNet.Core;

namespace VISOR.Tests
{
    public class SessionInfoYamlTests
    {
        // Made-up session info in iRacing's layout: unquoted values, one-space indents, lists
        // written "- Key: value" at their parent's indent. No real names or IDs. A raw string
        // takes the source file's line endings, which are CRLF in a Windows checkout, so they're
        // fixed to "\n" here (iRacing's own) and the multi-line edits below find their text.
        private static readonly string Session = """
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

        private static TelemetrySessionInfo ReadOrFail(string yaml, string expectedAttempt)
        {
            var info = SessionInfoYaml.Parse<TelemetrySessionInfo>(yaml, out string outcome);
            Assert.True(info != null, $"not read: {outcome}");
            Assert.Equal(expectedAttempt, outcome);
            return info!;
        }

        private static string Json(TelemetrySessionInfo info) => JsonSerializer.Serialize(info);

        private static string SyntaxError(string yaml)
        {
            var ex = Assert.Throws<SyntaxErrorException>(() =>
            {
                var parser = new Parser(new StringReader(yaml));
                while (parser.MoveNext()) { }
            });
            return ex.Message;
        }

        // --- Well-formed session info: the SDK's path, untouched ---

        [Fact]
        public void WellFormed_IsReadAsIs_AndLeftToTheSdk()
        {
            Assert.True(SessionInfoYaml.IsWellFormed(Session));
            Assert.False(SessionInfoYaml.WouldSdkFail(Session));

            var info = ReadOrFail(Session, "as-is");
            Assert.Equal("Test Raceway", info.WeekendInfo.TrackDisplayName);
            Assert.Equal(new[] { 0, 1, 2 }, info.DriverInfo.Drivers.Select(d => d.CarIdx));
            Assert.Equal(2, info.SessionInfo.Sessions.Count);
        }

        [Fact]
        public void Repair_ChangesNoValue_InWellFormedSessionInfo()
        {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            var original = deserializer.Deserialize<TelemetrySessionInfo>(Session);
            var repaired = deserializer.Deserialize<TelemetrySessionInfo>(SessionInfoYaml.Repair(Session));

            // Every value quoted, numbers included, reads back the same.
            Assert.Equal(Json(original), Json(repaired));
        }

        // --- What the SDK can't read, and VISOR can ---

        [Fact]
        public void LineBreakInAValue_IsTheErrorSeenOnTheRig_AndIsRepaired()
        {
            string broken = Session.Replace("ClubName: Test Club\n   CarClassID: 2268\n   CarScreenName: Test GT4\n   CarIsPaceCar: 0\n - CarIdx: 2",
                                            "ClubName: Test\n  Club\n   CarClassID: 2268\n   CarScreenName: Test GT4\n   CarIsPaceCar: 0\n - CarIdx: 2");
            Assert.NotEqual(Session, broken);

            // The message the SDK logged in the 9 October event.
            Assert.Equal("While scanning a multiline plain scalar, found invalid mapping.", SyntaxError(broken));
            Assert.True(SessionInfoYaml.WouldSdkFail(broken));

            var info = ReadOrFail(broken, "VISOR repair");
            Assert.Equal("Test Club", info.DriverInfo.Drivers[1].ClubName);
            Assert.Equal(3, info.DriverInfo.Drivers.Count);
            Assert.Equal(2268, info.DriverInfo.Drivers[1].CarClassID);
        }

        [Fact]
        public void ColonInAFieldTheSdkDoesNotQuote_IsRepaired()
        {
            string broken = Session.Replace("CarScreenName: Safety Car", "CarScreenName: Safety Car: Spec 2");

            Assert.True(SessionInfoYaml.WouldSdkFail(broken));
            Assert.Equal("Safety Car: Spec 2", ReadOrFail(broken, "VISOR repair").DriverInfo.Drivers[0].CarScreenName);
        }

        [Fact]
        public void BracketAtTheStartOfAValue_IsRepaired()
        {
            // YamlDotNet fails these with InvalidOperationException rather than a YamlException.
            string broken = Session.Replace("ClubName: Test Club\n   CarClassID: 2268\n   CarScreenName: Test GT4\n   CarIsPaceCar: 0\n - CarIdx: 2",
                                            "ClubName: {Test Club\n   CarClassID: 2268\n   CarScreenName: Test GT4\n   CarIsPaceCar: 0\n - CarIdx: 2");

            Assert.True(SessionInfoYaml.WouldSdkFail(broken));
            Assert.Equal("{Test Club", ReadOrFail(broken, "VISOR repair").DriverInfo.Drivers[1].ClubName);
        }

        [Fact]
        public void ColonInAName_IsRescuedByTheSdkItself()
        {
            string broken = Session.Replace("UserName: Alex Example", "UserName: Alex: Example");

            Assert.False(SessionInfoYaml.IsWellFormed(broken));
            Assert.False(SessionInfoYaml.WouldSdkFail(broken));   // so VISOR leaves it to the SDK
            Assert.Equal("Alex: Example", ReadOrFail(broken, "SDK quoting").DriverInfo.Drivers[1].UserName);
        }

        [Fact]
        public void Unreadable_ReturnsNull_WithTheError()
        {
            var info = SessionInfoYaml.Parse<TelemetrySessionInfo>("DriverInfo:\n Drivers:\n - CarIdx: 0\n  - : :\n", out string outcome);

            Assert.Null(info);
            Assert.False(string.IsNullOrEmpty(outcome));
        }

        // --- The SDK's quoting, reproduced exactly ---

        [Fact]
        public void QuoteSdkFields_QuotesOnlyTheSixNameFields()
        {
            string quoted = SessionInfoYaml.QuoteSdkFields("   UserName: O'Brien\n   ClubName: Test Club\n   TeamName: 'Already'\n   Initials: \n");

            Assert.Equal("   UserName: 'O''Brien'\n   ClubName: Test Club\n   TeamName: 'Already'\n   Initials: \n", quoted);
        }

        [Fact]
        public void QuoteSdkFields_KeepsWindowsLineEndings()
        {
            Assert.Equal("   UserName: 'A: B'\r\n   CarIdx: 1\r\n",
                SessionInfoYaml.QuoteSdkFields("   UserName: A: B\r\n   CarIdx: 1\r\n"));
        }

        // --- VISOR's repair ---

        [Fact]
        public void Repair_QuotesValues_AndLeavesStructureAlone()
        {
            string repaired = SessionInfoYaml.Repair("DriverInfo:\n Drivers:\n - CarIdx: 0\n   UserName: O'Brien\n   TeamName: 'Already'\n   AbbrevName: \n");

            Assert.Equal("DriverInfo:\n Drivers:\n - CarIdx: '0'\n   UserName: 'O''Brien'\n   TeamName: 'Already'\n   AbbrevName: \n", repaired);
        }

        [Fact]
        public void Repair_JoinsALineBreak_BackIntoItsValue()
        {
            Assert.Equal("   ClubName: 'Test Club'\n   CarIdx: '1'",
                SessionInfoYaml.Repair("   ClubName: Test\nClub\n   CarIdx: 1"));
        }

        [Fact]
        public void Repair_HandlesWindowsLineEndings()
        {
            Assert.Equal("   ClubName: 'Test Club'\n   CarIdx: '1'\n",
                SessionInfoYaml.Repair("   ClubName: Test\r\n Club\r\n   CarIdx: 1\r\n"));
        }

        // --- When VISOR takes over (SessionInfoFallback) ---

        [Fact]
        public void Fallback_LeavesReadableUpdatesToTheSdk_UntilOneFails()
        {
            string broken = Session.Replace("CarScreenName: Safety Car", "CarScreenName: Safety Car: Spec 2");
            var fallback = new SessionInfoFallback();

            Assert.Null(fallback.Read(Session, wellFormed: true));
            Assert.False(fallback.IsActive);

            var info = fallback.Read(broken, SessionInfoYaml.IsWellFormed(broken));
            Assert.NotNull(info);

            // Once VISOR's result is applied it reads every update itself, readable ones included,
            // so the two sources never alternate.
            fallback.Activate();
            Assert.True(fallback.IsActive);
            Assert.NotNull(fallback.Read(Session, wellFormed: true));

            // A disconnect hands it back to the SDK.
            fallback.Reset();
            Assert.False(fallback.IsActive);
            Assert.Null(fallback.Read(Session, wellFormed: true));
        }

        [Fact]
        public void Fallback_LeavesUpdatesTheSdkCanRescue_ToTheSdk()
        {
            string rescued = Session.Replace("UserName: Alex Example", "UserName: Alex: Example");

            Assert.Null(new SessionInfoFallback().Read(rescued, wellFormed: false));
        }

        [Fact]
        public void Fallback_ReturnsNull_WhenNothingCanReadIt()
        {
            Assert.Null(new SessionInfoFallback().Read("DriverInfo:\n Drivers:\n - CarIdx: 0\n  - : :\n", wellFormed: false));
        }
    }
}
