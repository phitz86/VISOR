using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Persists each car's learned torque statistics to
    /// %LOCALAPPDATA%\VISOR\ShiftModels\&lt;CarPath&gt;.json.
    ///
    /// Local only; nothing is sent anywhere. Files are treated as untrusted input on load: size
    /// capped, strictly typed, shape- and range-checked, and discarded (then relearned) if
    /// anything is off. A model saved for a different iRacing car build is discarded too, since a
    /// car update can change the engine.
    /// </summary>
    public sealed class ShiftModelStore
    {
        public const int SchemaVersion = 1;
        private const long MaxFileBytes = 2 * 1024 * 1024;
        private const double MaxAbsValue = 1e15;

        private static readonly Regex UnsafeChars = new("[^A-Za-z0-9_-]", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        private readonly string _directory;

        public ShiftModelStore(string directory)
        {
            _directory = directory;
        }

        /// <summary>
        /// Maps an iRacing CarPath (e.g. "mx5 mx52016") to a safe file name, or null if nothing
        /// usable remains. Only [A-Za-z0-9_-] survive, which rules out path separators, "..",
        /// drive letters and reserved characters.
        /// </summary>
        public static string? SafeFileStem(string carPath)
        {
            if (string.IsNullOrWhiteSpace(carPath)) return null;
            string stem = UnsafeChars.Replace(carPath.Trim(), "_");
            if (stem.Length > 64) stem = stem.Substring(0, 64);
            return stem.Trim('_').Length == 0 ? null : stem;
        }

        private string? PathFor(string carPath)
        {
            string? stem = SafeFileStem(carPath);
            return stem == null ? null : Path.Combine(_directory, stem + ".json");
        }

        /// <summary>
        /// Loads the saved model for a car. Returns null (with a reason) when there is none, it's
        /// for another car build, or it fails validation.
        /// </summary>
        public ShiftModelState? Load(string carPath, string carVersion, out string reason)
        {
            reason = string.Empty;
            string? path = PathFor(carPath);
            if (path == null) { reason = "no usable car identifier"; return null; }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { reason = "no saved model"; return null; }
                if (info.Length > MaxFileBytes) { reason = $"file too large ({info.Length} bytes)"; return null; }

                var dto = JsonSerializer.Deserialize<ShiftModelFile>(File.ReadAllBytes(path), JsonOptions);
                if (dto == null) { reason = "empty file"; return null; }

                if (dto.Schema != SchemaVersion) { reason = $"schema {dto.Schema} != {SchemaVersion}"; return null; }
                if (!string.Equals(dto.CarPath, carPath, StringComparison.Ordinal)) { reason = "file belongs to a different car"; return null; }
                if (!string.Equals(dto.CarVersion, carVersion, StringComparison.Ordinal))
                {
                    reason = $"car updated ({dto.CarVersion} -> {carVersion}); relearning";
                    return null;
                }
                if (dto.BinWidthRpm != ShiftPointLearner.BinWidthRpm || dto.BinCount != ShiftPointLearner.BinCount)
                {
                    reason = "model shape changed";
                    return null;
                }

                int n = ShiftPointLearner.ParamCount;
                if (dto.AtaUpper == null || dto.AtaUpper.Length != n * (n + 1) / 2 ||
                    dto.Atb == null || dto.Atb.Length != n ||
                    dto.BinWeights == null || dto.BinWeights.Length != ShiftPointLearner.BinCount ||
                    dto.SampleCount < 0)
                {
                    reason = "malformed arrays";
                    return null;
                }
                if (!AllSane(dto.AtaUpper) || !AllSane(dto.Atb) || !AllSane(dto.BinWeights))
                {
                    reason = "out-of-range values";
                    return null;
                }
                foreach (var w in dto.BinWeights)
                    if (w < 0) { reason = "negative bin weight"; return null; }

                return new ShiftModelState(dto.AtaUpper, dto.Atb, dto.BinWeights, dto.SampleCount);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                reason = $"unreadable ({ex.GetType().Name})";
                return null;
            }
        }

        /// <summary>
        /// Saves a car's model. Writes to a temp file then swaps it in, so a crash mid-write
        /// can't leave a truncated model behind. Returns false (with a reason) on failure.
        /// </summary>
        public bool Save(string carPath, string carVersion, ShiftModelState state, out string reason)
        {
            reason = string.Empty;
            string? path = PathFor(carPath);
            if (path == null) { reason = "no usable car identifier"; return false; }

            try
            {
                Directory.CreateDirectory(_directory);
                var dto = new ShiftModelFile
                {
                    Schema = SchemaVersion,
                    CarPath = carPath,
                    CarVersion = carVersion,
                    BinWidthRpm = ShiftPointLearner.BinWidthRpm,
                    BinCount = ShiftPointLearner.BinCount,
                    SampleCount = state.SampleCount,
                    AtaUpper = state.AtaUpper,
                    Atb = state.Atb,
                    BinWeights = state.BinWeights
                };
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                reason = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool AllSane(double[] values)
        {
            foreach (var v in values)
                if (!double.IsFinite(v) || Math.Abs(v) > MaxAbsValue) return false;
            return true;
        }

        /// <summary>On-disk shape. Plain, strictly typed properties only.</summary>
        private sealed class ShiftModelFile
        {
            public int Schema { get; set; }
            public string CarPath { get; set; } = string.Empty;
            public string CarVersion { get; set; } = string.Empty;
            public int BinWidthRpm { get; set; }
            public int BinCount { get; set; }
            public long SampleCount { get; set; }
            public double[]? AtaUpper { get; set; }
            public double[]? Atb { get; set; }
            public double[]? BinWeights { get; set; }
        }
    }
}
