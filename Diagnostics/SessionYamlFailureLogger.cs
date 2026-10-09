using System;
using System.IO;
using YamlDotNet.Core;

namespace VISOR.Diagnostics
{
#if DEBUG
    /// <summary>
    /// DEBUG-ONLY: saves a session-info YAML that won't parse, with the parser's error position,
    /// so the construct that defeats the SDK can be found. The SDK parses session info itself and
    /// only reports the exception, never the text, and its repair pass only quotes a handful of
    /// known fields; anything else in iRacing's unquoted YAML that confuses the parser leaves
    /// VISOR with no session data at all.
    ///
    /// Each raw update is run through the same YAML parser the SDK uses (syntax only; no object
    /// tree is built, so it costs little). Only failures are written, one file per distinct error
    /// and at most five per run (updates repeat every few seconds), to
    /// %LOCALAPPDATA%\VISOR\Diagnostics\SessionYaml. The files hold every driver's name and
    /// iRacing ID: check before sharing one.
    /// </summary>
    public sealed class SessionYamlFailureLogger
    {
        private const int MaxFilesPerRun = 5;

        private string _lastFailureKey = string.Empty;
        private int _filesWritten;

        /// <summary>Called with each raw session-info update, on the SDK's session-info thread.</summary>
        public void Check(string yaml)
        {
            if (string.IsNullOrEmpty(yaml) || _filesWritten >= MaxFilesPerRun)
                return;

            try
            {
                var parser = new Parser(new StringReader(yaml));
                while (parser.MoveNext()) { }
            }
            catch (Exception ex)
            {
                // Usually a YamlException carrying the error's position, but some malformed input
                // (e.g. a value starting with '{' or '[') makes YamlDotNet throw
                // InvalidOperationException instead, with no position.
                Mark? position = ex is YamlException yamlEx ? yamlEx.Start : null;
                string key = $"{position?.Line}:{position?.Column}:{ex.GetType().Name}:{ex.Message}";
                if (key == _lastFailureKey)
                    return;
                _lastFailureKey = key;
                Save(yaml, ex, position);
            }
        }

        private void Save(string yaml, Exception ex, Mark? position)
        {
            try
            {
                string dir = Path.Combine(Log.GetDiagnosticsDirectory(), "SessionYaml");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"parse-failure_{DateTime.Now:yyyyMMdd-HHmmss}_{_filesWritten + 1}.yaml");
                string where = position is { } p ? $"line {p.Line}, column {p.Column}" : "an unknown position";
                string error = $"{ex.GetType().Name}: {ex.Message}";

                File.WriteAllText(path,
                    $"# YAML parse error at {where}: {error}\n" +
                    "# Contains every driver's name and iRacing ID - check before sharing.\n" + yaml);
                _filesWritten++;

                Log.Warning($"[SessionYaml] session info failed to parse at {where} ({error}); saved to {path}" +
                            (position is { } at ? "\n" + Excerpt(yaml, at.Line) : string.Empty));
            }
            catch (Exception saveEx)
            {
                Log.Error("[SessionYaml] could not save the failing session info", saveEx);
            }
        }

        // The offending line with a few either side, so the log alone usually shows it. A value
        // broken across lines is reported on the line after the break, so the lines before the
        // error matter most.
        private static string Excerpt(string yaml, long errorLine)
        {
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            int target = (int)Math.Clamp(errorLine, 1, Math.Max(lines.Length, 1));
            int from = Math.Max(target - 4, 1), to = Math.Min(target + 2, lines.Length);
            var excerpt = new System.Text.StringBuilder();
            for (int n = from; n <= to; n++)
                excerpt.Append(n == target ? ">> " : "   ").Append(n).Append(": ").AppendLine(lines[n - 1]);
            return excerpt.ToString();
        }
    }
#endif
}
