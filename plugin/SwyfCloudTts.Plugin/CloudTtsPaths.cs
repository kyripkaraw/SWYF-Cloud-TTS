using System;
using System.IO;
using UnityEngine;

namespace SwyfCloudTts.Plugin
{
    internal static class CloudTtsPaths
    {
        public static string StateDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SWYF-CloudTTS");

        public static string ConfigPath => Path.Combine(StateDirectory, "cloud-tts.config.json");

        public static string CacheDirectory => Path.Combine(StateDirectory, "cache");

        public static string RuntimeWorkerPath => Path.Combine(
            Application.dataPath,
            "StreamingAssets",
            "kolkata-qwen-runtime",
            "QwenTtsWorker.exe");

        /// <summary>Copy kept by the installer; still the cloud worker after Steam restores the original.</summary>
        public static string AppWorkerPath => Path.Combine(StateDirectory, "bin", "QwenTtsWorker.exe");

        public static string TestWorkerPath => File.Exists(AppWorkerPath) ? AppWorkerPath : RuntimeWorkerPath;

        public static int ClearCache()
        {
            try
            {
                if (!Directory.Exists(CacheDirectory))
                {
                    return 0;
                }

                int removed = 0;
                foreach (string file in Directory.EnumerateFiles(CacheDirectory))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warning("cannot delete cached file " + file + ": " + ex.Message);
                    }
                }

                return removed;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("cannot clear audio cache: " + ex.Message);
                return 0;
            }
        }
    }
}
