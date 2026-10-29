using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace SwyfCloudTts.Plugin
{
    /// Points the game's Qwen voice at the cloud without any worker process. The game talks to
    /// Qwen only through Kolkata.QwenTts.QwenWorkerClient (start, Load, Synthesize), so those are
    /// answered here by <see cref="SpeechEngine"/>. The checks that would stop the game from
    /// using Qwen at all are answered too: the hardware gate (a 5.5 GB GPU), the checksum of the
    /// worker's runtime files, the model download (2 GB since 0.1.95) and, since 0.1.95, the
    /// per-language model order that puts Pocket TTS before Qwen for Russian, none of which the
    /// cloud needs. With the provider set to "passthrough" nothing is intercepted and the game's
    /// own model runs. Members are looked up by name, so a game update that renames one switches
    /// that part off with a log line instead of keeping the plugin from loading.
    internal static class CloudTtsQwenPatches
    {
        private const string ClientType = "Kolkata.QwenTts.QwenWorkerClient";

        private const int HookCount = 7;

        private static readonly object Gate = new object();

        /// Worker clients this plugin answers for. A client started while the provider was
        /// "passthrough" keeps its real process even if the setting changes later.
        private static readonly ConditionalWeakTable<object, object> Intercepted = new ConditionalWeakTable<object, object>();

        private static SpeechEngine _engine;
        private static DateTime _configStamp;

        /// QwenWorkerClient.Backend (0.1.95), which the game logs after Load.
        private static PropertyInfo _backend;

        public static void Apply(Harmony harmony)
        {
            Type client = CloudTtsPlugin.FindType(ClientType);
            if (client == null)
            {
                PluginLog.Error(ClientType + " was not found; the game's voice cannot be redirected.");
                return;
            }

            // 0.1.95 loads one model file (model, backend); 0.1.83 loaded two (talker, codec, backend).
            MethodInfo load = AccessTools.Method(client, "Load", new[] { typeof(string), typeof(string) }) ??
                              AccessTools.Method(client, "Load", new[] { typeof(string), typeof(string), typeof(string) });
            _backend = AccessTools.Property(client, "Backend");

            int applied = 0;
            applied += Patch(harmony, client.GetConstructor(new[] { typeof(string) }), nameof(ConstructorPrefix));
            applied += Patch(harmony, load, nameof(LoadPrefix));
            applied += Patch(harmony, AccessTools.Method(client, "Synthesize",
                    new[] { typeof(string), typeof(string), typeof(float[]), typeof(Action<float[]>), typeof(CancellationToken) }),
                nameof(SynthesizePrefix));
            applied += Patch(harmony, AccessTools.Method(client, "TryChooseBackend", new[] { typeof(string).MakeByRefType() }),
                nameof(TryChooseBackendPrefix));
            applied += Patch(harmony, AccessTools.Method(CloudTtsPlugin.FindType("Kolkata.QwenTts.QwenRuntimeFiles"), "Validate"),
                nameof(ValidatePrefix));
            applied += Patch(harmony, AccessTools.Method(CloudTtsPlugin.FindType("Kolkata.QwenTts.QwenTtsModels"), "VerifyAsync"),
                nameof(VerifyModelPrefix));
            applied += Patch(harmony, AccessTools.Method(CloudTtsPlugin.FindType(CloudTtsModelOrder.PreferencesType), "PrefersQwen"),
                nameof(PrefersQwenPostfix), postfix: true);
            PluginLog.Info("Qwen voice redirected to the cloud: " + applied + " of " + HookCount + " hooks in place.");

            RefreshGameState();
        }

        private static int Patch(Harmony harmony, MethodBase target, string hook, bool postfix = false)
        {
            if (target == null)
            {
                PluginLog.Warning("hook " + hook + " skipped: the game method was not found.");
                return 0;
            }

            try
            {
                var method = new HarmonyMethod(typeof(CloudTtsQwenPatches), hook);
                if (postfix)
                {
                    harmony.Patch(target, postfix: method);
                }
                else
                {
                    harmony.Patch(target, prefix: method);
                }

                return 1;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("hook " + hook + " failed: " + ex.Message);
                return 0;
            }
        }

        /// The hardware and download checks may have run before the hooks existed.
        private static void RefreshGameState()
        {
            try
            {
                Type models = CloudTtsPlugin.FindType("Kolkata.QwenTts.QwenTtsModels");
                PropertyInfo cacheRoot = models?.GetProperty("CacheRoot", BindingFlags.Public | BindingFlags.Static);
                if (cacheRoot == null || cacheRoot.GetValue(null, null) == null)
                {
                    return;
                }

                models.GetMethod("RefreshPreference", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                PluginLog.Debug("could not refresh the game's Qwen state: " + ex.Message);
            }
        }

        /// The engine for the current config, rebuilt whenever the settings card saves it.
        private static SpeechEngine Engine()
        {
            lock (Gate)
            {
                DateTime stamp = Stamp();
                if (_engine != null && stamp == _configStamp)
                {
                    return _engine;
                }

                Config config = Config.Load();
                Log.Init(config.StateDirectory, config.Logging.Verbose);
                Log.Info($"SWYF Cloud TTS {PluginInfo.Version} in game (BepInEx); config: {config.Path}");

                SpeechEngine previous = _engine;
                _engine = SpeechEngine.Create(config);
                _configStamp = stamp;
                Log.Info($"provider={_engine.ProviderName}{(_engine.PassthroughOnly ? " (the game's own model)" : "")}, " +
                         $"cache={_engine.CacheEnabled}");

                try
                {
                    previous?.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug("disposing the previous engine failed: " + ex.Message);
                }

                return _engine;
            }
        }

        private static DateTime Stamp()
        {
            try
            {
                string path = Config.ResolvePath();
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool CloudSpeaks()
        {
            try
            {
                return !Engine().PassthroughOnly;
            }
            catch (Exception ex)
            {
                PluginLog.Error("cannot read the Cloud TTS config: " + ex.Message);
                return true;
            }
        }

        private static bool ConstructorPrefix(object __instance)
        {
            if (!CloudSpeaks())
            {
                return true;
            }

            Intercepted.Add(__instance, Gate);
            return false;
        }

        private static bool LoadPrefix(object __instance, string backend)
        {
            if (!Intercepted.TryGetValue(__instance, out _))
            {
                return true;
            }

            try
            {
                // The game accepts cuda, vulkan or cpu here; the cloud needs neither GPU.
                _backend?.SetValue(__instance, "cpu", null);
            }
            catch (Exception ex)
            {
                Log.Debug("could not set the worker backend: " + ex.Message);
            }

            Log.Info($"load: backend={backend} (models not needed, the cloud speaks)");
            return false;
        }

        private static bool SynthesizePrefix(object __instance, string text, string language, float[] reference,
                                             Action<float[]> emit, CancellationToken token)
        {
            if (!Intercepted.TryGetValue(__instance, out _))
            {
                return true;
            }

            Engine().Speak(text, language, reference ?? Array.Empty<float>(), emit, null, token);
            return false;
        }

        private static bool TryChooseBackendPrefix(ref bool __result, ref string backend)
        {
            if (!CloudSpeaks())
            {
                return true;
            }

            backend = "CPU";
            __result = true;
            return false;
        }

        private static bool ValidatePrefix() => !CloudSpeaks();

        private static void PrefersQwenPostfix(object language, ref bool __result)
        {
            try
            {
                SpeechEngine engine = Engine();
                __result = CloudTtsModelOrder.PrefersQwen(language, __result,
                    engine.PassthroughOnly ? "passthrough" : engine.ProviderName, null);
            }
            catch (Exception ex)
            {
                PluginLog.Debug("the voice model order was left to the game: " + ex.Message);
            }
        }

        private static bool VerifyModelPrefix(ref Task<bool> __result)
        {
            if (!CloudSpeaks())
            {
                return true;
            }

            __result = Task.FromResult(true);
            return false;
        }
    }
}
