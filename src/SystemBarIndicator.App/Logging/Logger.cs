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
        // Past this size the log is moved to app.1.log (replacing the previous one) and a
        // fresh file is started, so a fault that repeats for weeks cannot fill the disk.
        private const long MaxLogBytes = 1024 * 1024;

        private static readonly object _lock = new();
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemBarIndicator", "app.log");
        private static readonly string _previousLogPath = Path.ChangeExtension(_logPath, ".1.log");

        /// <summary>The log file — the update swap script appends its outcome to it.</summary>
        public static string LogPath => _logPath;

        /// <summary>
        /// Logs an unexpected failure with its stack trace — the message alone rarely says
        /// where a fault in a sensor library came from.
        /// </summary>
        public static void Error(string context, Exception ex)
        {
            Write($"ERROR [{context}] {ex}");
        }

        public static void Warn(string message)
        {
            Write($"WARN  {message}");
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
                    var info = new FileInfo(_logPath);
                    if (info.Exists && info.Length > MaxLogBytes)
                        File.Move(_logPath, _previousLogPath, overwrite: true);
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
