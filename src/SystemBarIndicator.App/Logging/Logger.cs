using System;
using System.IO;

namespace SystemBarIndicator
{
    /// <summary>
    /// A minimal thread-safe logger. Writes to the debug output and to
    /// %LOCALAPPDATA%\SystemBarIndicator\app.log. It never throws.
    /// </summary>
    internal static class Logger
    {
        private static readonly object _lock = new();
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemBarIndicator", "app.log");

        public static void Error(string context, Exception ex)
        {
            Write($"ERROR [{context}] {ex.GetType().Name}: {ex.Message}");
        }

        public static void Warn(string message)
        {
            Write($"WARN  {message}");
        }

        public static void Info(string message)
        {
            Write($"INFO  {message}");
        }

        private static void Write(string line)
        {
            string stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}";
            System.Diagnostics.Debug.WriteLine(stamped);
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                    File.AppendAllText(_logPath, stamped + Environment.NewLine);
                }
            }
            catch
            {
                // logging must never disrupt the application
            }
        }
    }
}
