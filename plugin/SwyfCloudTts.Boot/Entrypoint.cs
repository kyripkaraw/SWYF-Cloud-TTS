using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Doorstop
{
    /// <summary>
    /// Unity Doorstop entrypoint. Doorstop loads this assembly before the game starts and
    /// invokes <see cref="Start"/>. BepInEx is deliberately not used: its preloader needs BCL
    /// APIs the game has stripped (System.IO.File.ReadAllLines and friends), it boots by
    /// IL-patching Application..cctor, and Harmony cannot work either because
    /// MethodBase.GetMethodBody is missing.
    /// </summary>
    public static class Entrypoint
    {
        private const string PluginFileName = "SwyfCloudTts.Plugin.dll";
        private const string PluginTypeName = "SwyfCloudTts.Plugin.CloudTtsPlugin";

        private static bool _hosted;

        public static void Start()
        {
            BootLog.Write("Doorstop entrypoint reached");

            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                BootLog.Write("subscribed to SceneManager.sceneLoaded");
            }
            catch (Exception ex)
            {
                BootLog.Write("could not subscribe to sceneLoaded: " + ex);
            }

            try
            {
                // Fallback for builds where the first scene is already loading.
                Camera.onPreCull += OnPreCull;
                BootLog.Write("subscribed to Camera.onPreCull");
            }
            catch (Exception ex)
            {
                BootLog.Write("could not hook Camera.onPreCull: " + ex);
            }
        }

        /// <summary>
        /// Unity scans loaded assemblies for this attribute, which covers the case where the
        /// assembly is present before the first scene is created.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void OnRuntimeInitialized()
        {
            BootLog.Write("RuntimeInitializeOnLoadMethod fired");
            CreateHost("runtime init");
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            CreateHost("scene " + scene.name);
        }

        private static void OnPreCull(Camera camera)
        {
            Camera.onPreCull -= OnPreCull;

            try
            {
                CreateHost("camera");
            }
            catch (Exception ex)
            {
                BootLog.Write("camera hook failed: " + ex);
            }
        }

        private static void CreateHost(string reason)
        {
            if (_hosted)
            {
                return;
            }

            _hosted = true;

            try
            {
                BootLog.Write("creating the plugin host (" + reason + ")");
                Type pluginType = LoadPluginType();
                if (pluginType == null)
                {
                    return;
                }

                var host = new GameObject("SWYF Cloud TTS");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent(pluginType);
                BootLog.Write("plugin host attached");

                try
                {
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                    Camera.onPreCull -= OnPreCull;
                }
                catch
                {
                    // Nothing depends on the unsubscribe succeeding.
                }
            }
            catch (Exception ex)
            {
                BootLog.Write("host creation failed: " + ex);
            }
        }

        private static Type LoadPluginType()
        {
            try
            {
                Assembly self = typeof(Entrypoint).Assembly;
                string directory = Path.GetDirectoryName(self.Location);
                string path = Path.Combine(directory ?? ".", PluginFileName);
                if (!File.Exists(path))
                {
                    BootLog.Write("plugin assembly is missing: " + path);
                    return null;
                }

                Assembly plugin = Assembly.LoadFrom(path);
                Type type = plugin.GetType(PluginTypeName, false, false);
                if (type == null)
                {
                    BootLog.Write("type " + PluginTypeName + " was not found in " + PluginFileName);
                }

                return type;
            }
            catch (Exception ex)
            {
                BootLog.Write("plugin assembly could not be loaded: " + ex);
                return null;
            }
        }
    }

    internal static class BootLog
    {
        private static readonly object Gate = new object();

        internal static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message;

            // UnityEngine.Debug must not be touched this early: Unity's managed log handler is
            // not initialised yet, and calling it here breaks Debug.Log for the whole process.
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SWYF-CloudTTS");
                Directory.CreateDirectory(directory);

                lock (Gate)
                {
                    File.AppendAllText(Path.Combine(directory, "boot.log"), line + Environment.NewLine);
                }
            }
            catch
            {
                // Nothing else we can do this early in the process.
            }
        }
    }
}
