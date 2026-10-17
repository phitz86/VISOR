using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace VISOR.Telemetry
{
    /// <summary>
    /// Reads iRacing's session-info YAML when the SDK can't.
    ///
    /// iRacing writes session info as YAML without quoting its values. The SDK (2.5.0) parses it
    /// as-is, retries with six name fields quoted, and gives up if both fail, which leaves VISOR
    /// with no session data for the whole event. Free text with a line break in it, or a colon in
    /// a field outside those six, defeats both attempts.
    ///
    /// This reproduces the SDK's two attempts (same YamlDotNet version and settings), so VISOR can
    /// tell in advance when the SDK will fail, and adds a third: <see cref="Repair"/>. Plain .NET
    /// plus YamlDotNet, so it is unit-tested directly.
    /// </summary>
    public static class SessionInfoYaml
    {
        // The SDK's second attempt (QuoteValuesYamlPreparationStrategy, SDK 2.5.0), verbatim.
        private static readonly Regex SdkQuotedFields = new(
            @"^(\s*(?:AbbrevName|TeamName|UserName|Initials|DriverSetupName|CameraName):)([ \t]+\S.*)$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // One line of iRacing's YAML: indentation, an optional "- " list marker, a key, then either
        // nothing (a nested mapping follows) or a space and the value.
        private static readonly Regex KeyLine = new(
            @"^(?<lead> *(?:- )?)(?<key>[A-Za-z0-9_\-]+):(?<rest>(?:[ \t].*)?)$",
            RegexOptions.Compiled);

        // The SDK's settings: exact property names, keys the model doesn't have are ignored.
        private static readonly IDeserializer Deserializer =
            new DeserializerBuilder().IgnoreUnmatchedProperties().Build();

        /// <summary>True when both of the SDK's attempts would fail to read this YAML.</summary>
        public static bool WouldSdkFail(string yaml) =>
            !IsWellFormed(yaml) && !IsWellFormed(QuoteSdkFields(yaml));

        /// <summary>
        /// Reads the YAML as the SDK would (as-is, then with its quoting), then with
        /// <see cref="Repair"/>. Returns null when all three fail. <paramref name="outcome"/> names
        /// the attempt that worked, or describes the last error.
        /// </summary>
        public static T? Parse<T>(string yaml, out string outcome) where T : class
        {
            Exception? lastError = null;
            foreach (var (name, prepare) in Attempts)
            {
                try
                {
                    var result = Deserializer.Deserialize<T>(prepare(yaml));
                    if (result != null)
                    {
                        outcome = name;
                        return result;
                    }
                }
                catch (Exception ex)   // YamlException, or InvalidOperationException from the scanner
                {
                    lastError = ex;
                }
            }
            outcome = lastError == null ? "empty document" : $"{lastError.GetType().Name}: {lastError.Message}";
            return null;
        }

        private static readonly (string Name, Func<string, string> Prepare)[] Attempts =
        {
            ("as-is", yaml => yaml),
            ("SDK quoting", QuoteSdkFields),
            ("VISOR repair", Repair),
        };

        /// <summary>
        /// Syntax check only: runs YamlDotNet's parser without building any objects, which is how
        /// the YAML fails when the SDK can't read it.
        /// </summary>
        public static bool IsWellFormed(string yaml)
        {
            try
            {
                var parser = new Parser(new StringReader(yaml));
                while (parser.MoveNext()) { }
                return true;
            }
            catch (Exception)   // YamlException, or InvalidOperationException for some malformed input
            {
                return false;
            }
        }

        /// <summary>The SDK's quoting of its six name fields, reproduced exactly.</summary>
        internal static string QuoteSdkFields(string yaml) => SdkQuotedFields.Replace(yaml, match =>
        {
            string key = match.Groups[1].Value;
            string value = match.Groups[2].Value;
            bool endsWithCr = value.EndsWith('\r');
            if (endsWithCr)
                value = value[..^1];

            string trimmed = value.TrimStart();
            if (IsQuoted(trimmed))
                return match.Value;
            return $"{key} '{trimmed.Replace("'", "''")}'{(endsWithCr ? "\r" : "")}";
        });

        /// <summary>
        /// VISOR's repair. A line that isn't a key line continues the value above it (a value with
        /// a line break in it), so it is joined back on with a space. Then every value is quoted,
        /// which makes colons, '#', brackets and other YAML punctuation inside it plain text.
        /// Quoted numbers still read into the model's int and float fields.
        ///
        /// Limitation: text after a line break that itself looks like "Word: text" can't be told
        /// apart from a real key, so it stays a line of its own.
        /// </summary>
        public static string Repair(string yaml)
        {
            var lines = yaml.Replace("\r\n", "\n").Split('\n');

            var joined = new List<string>(lines.Length);
            foreach (string line in lines)
            {
                bool continuation = joined.Count > 0
                    && !string.IsNullOrWhiteSpace(line)
                    && line != "---" && line != "..."
                    && !KeyLine.IsMatch(line);
                if (continuation)
                    joined[^1] = joined[^1].TrimEnd() + " " + line.Trim();
                else
                    joined.Add(line);
            }

            var repaired = new StringBuilder(yaml.Length + yaml.Length / 8);
            for (int i = 0; i < joined.Count; i++)
            {
                if (i > 0)
                    repaired.Append('\n');

                var match = KeyLine.Match(joined[i]);
                string value = match.Success ? match.Groups["rest"].Value.Trim() : string.Empty;
                if (value.Length == 0 || IsQuoted(value))
                {
                    repaired.Append(joined[i]);
                    continue;
                }
                repaired.Append(match.Groups["lead"].Value)
                        .Append(match.Groups["key"].Value)
                        .Append(": '")
                        .Append(value.Replace("'", "''"))
                        .Append('\'');
            }
            return repaired.ToString();
        }

        private static bool IsQuoted(string value) =>
            value.Length >= 2 &&
            ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '"' && value[^1] == '"'));
    }
}
