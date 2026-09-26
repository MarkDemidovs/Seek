using System;
using System.IO;

namespace Seek
{
    /// <summary>Appends unexpected errors to seek.log next to the index, so failures are never silent.</summary>
    internal static class Log
    {
        static readonly object gate = new object();

        public static string FilePath;

        public static void Error(Exception ex)
        {
            Write(ex == null ? "Unknown error" : ex.ToString());
        }

        public static void Write(string message)
        {
            if (FilePath == null) return;
            try
            {
                lock (gate)
                {
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 1 << 20) info.Delete(); // keep it small
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
