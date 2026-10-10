using System.Collections.Generic;
using VISOR.Telemetry;
using VISOR.ViewModels;

namespace VISOR.Tests
{
    /// <summary>Session data for scripted races: a driver list, classes and qualifying order.</summary>
    internal sealed class FakeSession : ISessionDataProvider
    {
        public bool IsDataReady { get; set; } = true;
        public string[] UserNames { get; } = new string[PositionFrame.CarCount];
        public string[] CarNumbers { get; } = new string[PositionFrame.CarCount];
        public int[] CarNumberRaw { get; } = new int[PositionFrame.CarCount];
        public int[] CarClassIDs { get; } = new int[PositionFrame.CarCount];
        public int[] CarClassColors { get; } = new int[PositionFrame.CarCount];
        public bool[] CarIsAI { get; } = new bool[PositionFrame.CarCount];
        public float[] CarClassEstLapTimes { get; } = new float[PositionFrame.CarCount];
        public int[] CurDriverIncidentCount { get; } = new int[PositionFrame.CarCount];
        public int IncidentLimit => 0;
        public int CurrentSessionNum => 0;
        public string SubSessionId => "1";
        public PlayerCarInfo? PlayerCar => null;

        public int GetSessionLaps(int sessionNum) => 0;
        public double GetSessionTimeSeconds(int sessionNum) => 0;
        public bool IsQualifyingSession(int sessionNum) => false;

        /// <summary>Practice and qualifying order by fastest lap, so the calculator stands aside.</summary>
        public bool FastestLapMode { get; set; }
        public bool ShouldUseFastestLapPositioning() => FastestLapMode;
        public bool ShouldHideRelativeDisplay() => false;
        public List<(int carIdx, float fastestTime, int classPosition, int overallPosition)> GetFastestLapPositioning() => new();
        public float[] GetQualifyResultsFastestTimes() => new float[PositionFrame.CarCount];

        /// <summary>Field-wide qualifying order, 1-based; 0 = no result.</summary>
        public int[] QualifyPositions { get; } = new int[PositionFrame.CarCount];
        public int[] GetQualifyResultsPositions() => (int[])QualifyPositions.Clone();
    }

    /// <summary>
    /// A scripted race: cars are placed and driven with plain numbers, and each <see cref="Frame"/>
    /// feeds one telemetry frame to a <see cref="PositionCalculator"/>. A car "on lap n at pct p"
    /// has CarIdxLap n, CarIdxLapDistPct p and CarIdxLapCompleted n - 1, as iRacing reports.
    /// </summary>
    internal sealed class Race
    {
        public readonly PositionCalculator Calc = new();
        public readonly FakeSession Session = new();

        public int SessionNum = 1;
        public int SessionState = SessionStates.Racing;
        public readonly float[] Pct = Filled(-1f);
        public readonly int[] Lap = new int[PositionFrame.CarCount];
        public readonly int[] LapCompleted = Filled(-1);
        public readonly bool[] OnPitRoad = new bool[PositionFrame.CarCount];
        public readonly int[] ClassPosition = new int[PositionFrame.CarCount];   // live grid arrays
        public readonly int[] Position = new int[PositionFrame.CarCount];

        private static T[] Filled<T>(T value)
        {
            var a = new T[PositionFrame.CarCount];
            System.Array.Fill(a, value);
            return a;
        }

        /// <summary>Adds a car to the session's driver list.</summary>
        public Race Car(int carIdx, int classId = 1)
        {
            Session.CarNumbers[carIdx] = (carIdx + 10).ToString();
            Session.UserNames[carIdx] = $"Driver {carIdx}";
            Session.CarClassIDs[carIdx] = classId;
            return this;
        }

        public void Place(int carIdx, int lap, float pct)
        {
            Lap[carIdx] = lap;
            Pct[carIdx] = pct;
            LapCompleted[carIdx] = lap - 1;
        }

        /// <summary>The car crosses S/F this frame: on to the next lap, just past the line.</summary>
        public void Cross(int carIdx, float pct = 0.002f) => Place(carIdx, Lap[carIdx] + 1, pct);

        /// <summary>Telemetry for the car stops (iRacing reports -1).</summary>
        public void LoseTelemetry(int carIdx)
        {
            Pct[carIdx] = -1f;
            LapCompleted[carIdx] = -1;
        }

        /// <summary>The car leaves the session's driver list (offline and AI cars do at the finish).</summary>
        public void LeaveSession(int carIdx) => Session.CarNumbers[carIdx] = string.Empty;

        public void Frame(int count = 1)
        {
            for (int i = 0; i < count; i++)
                Calc.Update(Build(), Session);
        }

        /// <summary>Moves each given car forward by <paramref name="pctPerFrame"/> every frame, crossing S/F as it goes.</summary>
        public void Drive(int frames, float pctPerFrame, params int[] cars)
        {
            for (int f = 0; f < frames; f++)
            {
                foreach (int c in cars)
                {
                    float next = Pct[c] + pctPerFrame;
                    if (next >= 1f)
                        Place(c, Lap[c] + 1, next - 1f);
                    else
                        Pct[c] = next;
                }
                Frame();
            }
        }

        public PositionFrame Build() => new()
        {
            SessionNum = SessionNum,
            SessionState = SessionState,
            CarIdxLapDistPct = (float[])Pct.Clone(),
            CarIdxLap = (int[])Lap.Clone(),
            CarIdxLapCompleted = (int[])LapCompleted.Clone(),
            CarIdxOnPitRoad = (bool[])OnPitRoad.Clone(),
            CarIdxClassPosition = (int[])ClassPosition.Clone(),
            CarIdxPosition = (int[])Position.Clone(),
            SessionFlags = 0,
            PlayerCarIdx = -1,
            CarIdxTrackSurface = new int[PositionFrame.CarCount],
            CarIdxBestLapTime = new float[PositionFrame.CarCount],
            CarIdxEstTime = new float[PositionFrame.CarCount],
        };

        public int ClassPos(int carIdx) => Calc.GetClassPosition(carIdx, Session.CarClassIDs[carIdx]);
        public int Overall(int carIdx) => Calc.GetOverallPosition(carIdx);
    }
}
