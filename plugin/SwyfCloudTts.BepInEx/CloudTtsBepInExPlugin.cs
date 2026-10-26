using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SwyfCloudTts.Plugin
{
    /// BepInEx entry point. Speech is redirected in process by <see cref="CloudTtsQwenPatches"/>;
    /// the settings card is hosted the same way the Doorstop build hosts it.
    [BepInPlugin(Guid, "SWYF Cloud TTS", PluginInfo.Version)]
    public sealed class CloudTtsBepInExPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.kyripkaraw.swyfcloudtts";

        private void Awake()
        {
            PluginLog.Info("SWYF Cloud TTS " + PluginInfo.Version + " (BepInEx) loading");

            try
            {
                CloudTtsQwenPatches.Apply(new Harmony(Guid));
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not redirect the game's Qwen voice: " + ex);
            }

            var host = new GameObject("SWYF Cloud TTS");
            DontDestroyOnLoad(host);
            host.AddComponent<CloudTtsPlugin>();
        }
    }
}
