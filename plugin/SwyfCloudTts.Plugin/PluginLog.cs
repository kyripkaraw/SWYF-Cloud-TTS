using System;
using System.IO;
using UnityEngine;

namespace SwyfCloudTts.Plugin
{
    /// Writes to %LOCALAPPDATA%\SWYF-CloudTTS\plugin.log and to the Unity log.
    internal static class PluginLog
    {
        private const long MaxLogBytes = 1024 * 1024;

        private static readonly object Gate = new object();
        private static string _path;

        private static string LogPath
        {
            get
            {
                if (_path == null)
                {
                    try
                    {
                        Directory.CreateDirectory(CloudTtsPaths.StateDirectory);
                    }
                    catch
                    {
                        // Without the directory the file log stays off; the Unity log still works.
                    }

                    _path = Path.Combine(CloudTtsPaths.StateDirectory, "plugin.log");
                }

                return _path;
            }
        }

        internal static void Info(string message) => Write("INFO", message);

        internal static void Warning(string message) => Write("WARN", message);

        internal static void Error(string message) => Write("ERROR", message);

        internal static void Debug(string message) => Write("DEBUG", message);

        private static void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message;
            try
            {
                UnityEngine.Debug.Log("[CloudTTS] " + line);
            }
            catch
            {
                // Logging must never break the game.
            }

            try
            {
                lock (Gate)
                {
                    var file = new FileInfo(LogPath);
                    if (file.Exists && file.Length > MaxLogBytes)
                    {
                        File.Delete(LogPath);
                    }

                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break the game.
            }
        }
    }
}
