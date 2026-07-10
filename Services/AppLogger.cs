using System;
using System.IO;

namespace WindowsLiveCaptionsReader.Services
{
    public static class AppLogger
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EnglishLearningAssistant");

        private static readonly string LogFile = Path.Combine(LogDir, "app.log");

        public static void Info(string message)  => Write("INFO ", message);
        public static void Warn(string message)  => Write("WARN ", message);
        public static void Error(string message, Exception? ex = null)
        {
            Write("ERROR", message);
            if (ex != null)
                Write("ERROR", $"  {ex.GetType().Name}: {ex.Message}");
        }

        private static void Write(string level, string message)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
