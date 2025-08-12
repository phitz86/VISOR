using System;
using System.IO;
using System.Text;

namespace VISOR.Diagnostics
{
    public static class ConsoleLogger
    {
        private static StreamWriter _logWriter;

        public static void Initialize(string outputDir)
        {
            try
            {
                Directory.CreateDirectory(outputDir);
                var logPath = Path.Combine(outputDir, $"VISOR-console-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
                _logWriter = new StreamWriter(logPath, append: true)
                {
                    AutoFlush = true
                };

                Console.SetOut(new MultiWriter(Console.Out, _logWriter));
                Console.SetError(new MultiWriter(Console.Error, _logWriter));

                Console.WriteLine($"[ConsoleLogger] Redirected console output to: {logPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConsoleLogger] Failed to redirect console: {ex.Message}");
            }
        }

        private class MultiWriter : TextWriter
        {
            private readonly TextWriter _a, _b;

            public MultiWriter(TextWriter a, TextWriter b)
            {
                _a = a; _b = b;
            }

            public override Encoding Encoding => _a.Encoding;

            public override void WriteLine(string value)
            {
                _a.WriteLine(value);
                _b.WriteLine(value);
            }

            public override void Write(char value)
            {
                _a.Write(value);
                _b.Write(value);
            }
        }
    }
}