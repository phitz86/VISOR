using VISOR.ViewModels;
using Xunit;

namespace VISOR.Tests
{
    public class FuelViewModelTests
    {
        [Fact]
        public void ShowsPlaceholder_UntilALapHasBeenMeasured()
        {
            var fuel = new FuelViewModel();
            Assert.Equal("--- Laps", fuel.FuelDisplay);

            fuel.Update(50f, 1);
            fuel.Update(49.5f, 1);   // fuel falling mid-lap, but no completed lap yet
            Assert.Equal("--- Laps", fuel.FuelDisplay);
        }

        [Fact]
        public void EstimatesLapsRemaining_FromLastLapsUsage()
        {
            var fuel = new FuelViewModel();
            fuel.Update(50f, 1);
            fuel.Update(47f, 2);     // 3 L used on lap 1

            Assert.Equal($"{47f / 3f:F1} Laps", fuel.FuelDisplay);
        }

        [Fact]
        public void UpdatesEstimate_WithCurrentFuel_DuringTheLap()
        {
            var fuel = new FuelViewModel();
            fuel.Update(50f, 1);
            fuel.Update(47f, 2);
            fuel.Update(45.5f, 2);   // half a lap later, same average

            Assert.Equal($"{45.5f / 3f:F1} Laps", fuel.FuelDisplay);
        }

        [Fact]
        public void AveragesOnlyTheLastThreeLaps()
        {
            var fuel = new FuelViewModel();
            // Lap usage: 6, 3, 3, 3. The 6 L lap (e.g. a standing start) drops out of the average.
            float[] levels = { 60f, 54f, 51f, 48f, 45f };
            for (int lap = 1; lap <= levels.Length; lap++)
                fuel.Update(levels[lap - 1], lap);

            Assert.Equal($"{45f / 3f:F1} Laps", fuel.FuelDisplay);
        }

        [Fact]
        public void IgnoresARefuel_WhenAveraging()
        {
            var fuel = new FuelViewModel();
            fuel.Update(20f, 1);
            fuel.Update(17f, 2);     // 3 L used
            fuel.Update(60f, 3);     // refuelled in the pits: "usage" is negative, not counted

            Assert.Equal($"{60f / 3f:F1} Laps", fuel.FuelDisplay);
        }

        [Fact]
        public void SkippedLap_IsNotCountedAsOneLapsUsage()
        {
            var fuel = new FuelViewModel();
            fuel.Update(50f, 1);
            fuel.Update(44f, 3);     // lap 2 never seen: 6 L over two laps must not read as one lap

            Assert.Equal("--- Laps", fuel.FuelDisplay);
        }

        [Fact]
        public void Reset_ClearsHistory()
        {
            var fuel = new FuelViewModel();
            fuel.Update(50f, 1);
            fuel.Update(47f, 2);
            fuel.Reset();

            Assert.Equal("--- Laps", fuel.FuelDisplay);

            // Lap numbers restart after a reset (new session).
            fuel.Update(30f, 1);
            Assert.Equal("--- Laps", fuel.FuelDisplay);
        }
    }
}
