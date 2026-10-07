using System.Text.Json;
using VISOR.Telemetry;
using Xunit;

namespace VISOR.Tests
{
    public sealed class ShiftModelStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "visor-shiftmodel-tests-" + Guid.NewGuid().ToString("N"));
        private readonly ShiftModelStore _store;

        private const string Car = "mx5 mx52016";
        private const string Version = "2025.09.10.03";

        public ShiftModelStoreTests() => _store = new ShiftModelStore(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static ShiftPointLearner TrainedLearner()
        {
            var learner = new ShiftPointLearner();
            var sim = new CarSimulator();
            for (int i = 0; i < 25; i++)
                foreach (var s in sim.Run(_ => CarSimulator.RedLine))
                    learner.AddSample(s);
            return learner;
        }

        [Fact]
        public void RoundTrip_ReproducesSameShiftPoints()
        {
            var original = TrainedLearner();
            Assert.True(_store.Save(Car, Version, original.ExportState(), out var saveReason), saveReason);

            var state = _store.Load(Car, Version, out var loadReason);
            Assert.NotNull(state);

            // Gear ratios are per-session, so give the restored model a little fresh driving
            // (as a real session would) before solving.
            var restored = new ShiftPointLearner();
            restored.ImportState(state!);
            Assert.Equal(original.SampleCount, restored.SampleCount);
            var sim = new CarSimulator(seed: 99);
            foreach (var s in sim.Run(_ => CarSimulator.RedLine)) restored.AddSample(s);

            var a = original.Solve(CarSimulator.RedLine, 6);
            var b = restored.Solve(CarSimulator.RedLine, 6);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.True(b[i].Confident, b[i].Reason);
                Assert.InRange(b[i].Rpm, a[i].Rpm - 50, a[i].Rpm + 50);
            }
        }

        [Fact]
        public void RoundTrip_KeepsGearRatios_AndOldFilesWithoutThemStillLoad()
        {
            var state = TrainedLearner().ExportState();
            Assert.NotNull(state.GearRatios);
            Assert.True(state.GearRatios![6] > 0);
            _store.Save(Car, Version, state, out _);
            var loaded = _store.Load(Car, Version, out _);
            Assert.Equal(state.GearRatios, loaded!.GearRatios);

            _store.Save(Car, Version, state with { GearRatios = null }, out _);
            Assert.NotNull(_store.Load(Car, Version, out var reason));
        }

        [Theory]
        [InlineData(5000.0)]    // implausible ratio
        [InlineData(-3.0)]
        public void Load_RejectsMalformedGearRatios(double bad)
        {
            var state = TrainedLearner().ExportState();
            var ratios = (double[])state.GearRatios!.Clone();
            ratios[2] = bad;
            _store.Save(Car, Version, state with { GearRatios = ratios }, out _);
            Assert.Null(_store.Load(Car, Version, out var reason));
            Assert.Equal("malformed gear ratios", reason);
        }

        [Fact]
        public void Load_ReturnsNull_WhenNothingSaved()
        {
            Assert.Null(_store.Load(Car, Version, out var reason));
            Assert.Equal("no saved model", reason);
        }

        [Fact]
        public void Load_Discards_WhenCarVersionChanged()
        {
            _store.Save(Car, Version, TrainedLearner().ExportState(), out _);
            Assert.Null(_store.Load(Car, "2026.01.01.01", out var reason));
            Assert.Contains("car updated", reason);
        }

        [Theory]
        [InlineData("../../Windows/System32/evil")]
        [InlineData("..\\..\\evil")]
        [InlineData("C:\\temp\\x")]
        [InlineData("/etc/passwd")]
        public void SafeFileStem_CannotEscapeDirectory(string carPath)
        {
            string? stem = ShiftModelStore.SafeFileStem(carPath);
            Assert.NotNull(stem);
            Assert.DoesNotContain("/", stem);
            Assert.DoesNotContain("\\", stem);
            Assert.DoesNotContain(":", stem);
            Assert.DoesNotContain("..", stem);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("../..")]
        public void SafeFileStem_RejectsUnusable(string carPath)
        {
            Assert.Null(ShiftModelStore.SafeFileStem(carPath));
        }

        [Fact]
        public void Load_RejectsFileForDifferentCar()
        {
            // Two CarPaths that sanitize to the same file name must not share a model.
            _store.Save("car one", Version, TrainedLearner().ExportState(), out _);
            Assert.Null(_store.Load("car_one", Version, out var reason));
            Assert.Equal("file belongs to a different car", reason);
        }

        [Fact]
        public void Load_RejectsMalformedJson()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath(Car), "{ not json");
            Assert.Null(_store.Load(Car, Version, out var reason));
            Assert.StartsWith("unreadable", reason);
        }

        [Fact]
        public void Load_RejectsWrongArrayShapes()
        {
            _store.Save(Car, Version, TrainedLearner().ExportState(), out _);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(FilePath(Car)))!;
            var edited = doc.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
            edited["Atb"] = new double[] { 1, 2, 3 };
            File.WriteAllText(FilePath(Car), JsonSerializer.Serialize(edited));

            Assert.Null(_store.Load(Car, Version, out var reason));
            Assert.Equal("malformed arrays", reason);
        }

        [Fact]
        public void Load_RejectsOutOfRangeValues()
        {
            var state = TrainedLearner().ExportState();
            state.Atb[0] = 1e300;
            _store.Save(Car, Version, state, out _);
            Assert.Null(_store.Load(Car, Version, out var reason));
            Assert.Equal("out-of-range values", reason);
        }

        [Fact]
        public void Load_RejectsOversizedFile()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(FilePath(Car), new byte[3 * 1024 * 1024]);
            Assert.Null(_store.Load(Car, Version, out var reason));
            Assert.StartsWith("file too large", reason);
        }

        private string FilePath(string carPath) => Path.Combine(_dir, ShiftModelStore.SafeFileStem(carPath) + ".json");
    }
}
