using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SwyfCloudTts.Plugin
{
    /// Since 0.1.83 the game only starts the Qwen worker on PCs with a 5.5 GB NVIDIA or AMD card
    /// and 15 GB of RAM (QwenWorkerClient.HardwareEligible); elsewhere every line goes to its local
    /// MOSS voice and the cloud worker is never asked. The cloud worker needs none of that, so
    /// QwenWorkerClient.TryChooseBackend is redirected in memory to a method that always agrees.
    /// Since 0.1.95 the game also asks Qwen only when it leads the per-language model order, which
    /// puts Pocket TTS first for Russian, so SpeechModelPreferences.PrefersQwen is redirected the
    /// same way (see <see cref="CloudTtsModelOrder"/>). Nothing on disk changes. Harmony cannot run
    /// on the stripped mscorlib, hence the jump is written by hand over the start of the method's
    /// compiled code.
    internal static class CloudTtsHardwareGate
    {
        private const uint PageExecuteReadWrite = 0x40;

        private static bool _applied;

        /// The provider as last read from the config, for the redirected model order.
        private static DateTime _configStamp = DateTime.MinValue;
        private static string _provider = "";
        private static string[] _localLanguages;

        public static void Apply(CloudTtsConfig config)
        {
            if (_applied)
            {
                return;
            }

            _applied = true;
            if (config.Provider == "passthrough")
            {
                PluginLog.Info("hardware check left to the game: the provider is the game's own Qwen.");
                return;
            }

            if (!CloudWorkerInstalled())
            {
                PluginLog.Warning("hardware check left to the game: the game's QwenTtsWorker.exe is not the cloud worker.");
                return;
            }

            if (IntPtr.Size != 8)
            {
                return;
            }

            try
            {
                RedirectModelOrder();
            }
            catch (Exception ex)
            {
                PluginLog.Warning("could not redirect the game's voice model order: " + ex.Message);
            }

            try
            {
                RedirectHardwareCheck();
            }
            catch (Exception ex)
            {
                PluginLog.Warning("could not redirect the game's hardware check: " + ex.Message);
            }
        }

        private static void RedirectHardwareCheck()
        {
            Type client = CloudTtsPlugin.FindType("Kolkata.QwenTts.QwenWorkerClient");
            MethodInfo target = client?.GetMethod(
                "TryChooseBackend", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string).MakeByRefType() }, null);
            if (target == null || target.ReturnType != typeof(bool))
            {
                PluginLog.Warning("QwenWorkerClient.TryChooseBackend was not found; the game's hardware check stays.");
                return;
            }

            if (Ask(target, out string before))
            {
                PluginLog.Info("the game already accepts this PC for Qwen (backend " + before + ").");
                return;
            }

            MethodInfo replacement = typeof(CloudTtsHardwareGate).GetMethod(
                nameof(CloudBackend), BindingFlags.NonPublic | BindingFlags.Static);
            Redirect(target, replacement);

            if (!Ask(target, out _))
            {
                PluginLog.Warning("the hardware check could not be redirected; the game keeps its local voices.");
                return;
            }

            PluginLog.Info("hardware check redirected: the game now sends lines to the cloud worker on this PC.");
            Refresh();
        }

        private static bool CloudBackend(out string backend)
        {
            backend = "CPU";
            return true;
        }

        private static void RedirectModelOrder()
        {
            Type preferences = CloudTtsPlugin.FindType(CloudTtsModelOrder.PreferencesType);
            Type option = CloudTtsPlugin.FindType("Kolkata.Speech.CallLanguageOption");
            MethodInfo target = preferences != null && option != null
                ? preferences.GetMethod("PrefersQwen", BindingFlags.Public | BindingFlags.Static, null, new[] { option }, null)
                : null;
            if (target == null || target.ReturnType != typeof(bool))
            {
                PluginLog.Info("the game has no per-language voice model order; nothing to redirect.");
                return;
            }

            MethodInfo replacement = typeof(CloudTtsHardwareGate).GetMethod(
                nameof(CloudPrefersQwen), BindingFlags.NonPublic | BindingFlags.Static);
            Redirect(target, replacement);
            PluginLog.Info("voice model order redirected: Qwen comes first for Russian while the mod speaks.");
        }

        /// Stands in for SpeechModelPreferences.PrefersQwen(CallLanguageOption), whose code it replaces.
        private static bool CloudPrefersQwen(object language)
        {
            bool game = CloudTtsModelOrder.GameAnswer(language);
            try
            {
                RefreshProvider();
                return CloudTtsModelOrder.PrefersQwen(language, game, _provider, _localLanguages);
            }
            catch
            {
                return game;
            }
        }

        /// The game asks several times a second while its model list is open, so the config is
        /// read again only after the card has saved it.
        private static void RefreshProvider()
        {
            string path = CloudTtsPaths.ConfigPath;
            DateTime stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (stamp == _configStamp && _provider.Length > 0)
            {
                return;
            }

            CloudTtsConfig config = CloudTtsConfig.Load();
            _provider = config.Provider;
            _localLanguages = null;
            _configStamp = stamp;
        }

        private static bool Ask(MethodInfo target, out string backend)
        {
            object[] args = { null };
            bool result = (bool)target.Invoke(null, args);
            backend = args[0] as string ?? "";
            return result;
        }

        /// Writes "mov rax, replacement; jmp rax" over the first bytes of the target.
        private static void Redirect(MethodInfo target, MethodInfo replacement)
        {
            // The stripped mscorlib has no GetFunctionPointer, so Mono itself is asked for the code.
            IntPtr from = mono_compile_method(target.MethodHandle.Value);
            IntPtr to = mono_compile_method(replacement.MethodHandle.Value);
            if (from == IntPtr.Zero || to == IntPtr.Zero)
            {
                throw new InvalidOperationException("Mono returned no code for the hardware check");
            }

            byte[] jump = new byte[12];
            jump[0] = 0x48;
            jump[1] = 0xB8;
            BitConverter.GetBytes(to.ToInt64()).CopyTo(jump, 2);
            jump[10] = 0xFF;
            jump[11] = 0xE0;

            if (!VirtualProtect(from, (UIntPtr)jump.Length, PageExecuteReadWrite, out uint old))
            {
                throw new InvalidOperationException("VirtualProtect failed (" + Marshal.GetLastWin32Error() + ")");
            }

            Marshal.Copy(jump, 0, from, jump.Length);
            VirtualProtect(from, (UIntPtr)jump.Length, old, out _);
            FlushInstructionCache(GetCurrentProcess(), from, (UIntPtr)jump.Length);
        }

        /// The game read the check once at startup and marked Qwen unavailable; asking it again
        /// picks up the new answer. Before the game has set up its model folder it will ask by itself.
        private static void Refresh()
        {
            Type models = CloudTtsPlugin.FindType("Kolkata.QwenTts.QwenTtsModels");
            if (models == null)
            {
                return;
            }

            PropertyInfo cacheRoot = models.GetProperty("CacheRoot", BindingFlags.Public | BindingFlags.Static);
            if (cacheRoot != null && cacheRoot.GetValue(null, null) == null)
            {
                return;
            }

            MethodInfo refresh = models.GetMethod("RefreshPreference", BindingFlags.Public | BindingFlags.Static);
            refresh?.Invoke(null, null);
        }

        /// The installer keeps a copy of the cloud worker; a Steam file check puts the game's own back.
        private static bool CloudWorkerInstalled()
        {
            try
            {
                string runtime = CloudTtsPaths.RuntimeWorkerPath;
                string copy = CloudTtsPaths.AppWorkerPath;
                return File.Exists(runtime) && File.Exists(copy) &&
                       new FileInfo(runtime).Length == new FileInfo(copy).Length;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("mono-2.0-bdwgc.dll")]
        private static extern IntPtr mono_compile_method(IntPtr method);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint oldProtection);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    }
}
