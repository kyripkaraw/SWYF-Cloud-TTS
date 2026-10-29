using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SwyfCloudTts.Setup
{
    internal enum InstallState
    {
        GameNotFound,
        NotInstalled,
        Installed,
        Outdated,
        NeedsRepair,
    }

    internal sealed class InstallStatus
    {
        public InstallState State;
        public string InstalledVersion = "";
        public bool CardInstalled;
        public bool CardBlocked;
    }

    /// A failure explained in words the player can act on; shown without a stack trace.
    internal sealed class SetupException : Exception
    {
        public SetupException(string message) : base(message)
        {
        }
    }

    /// Installs the mod into one game folder:
    /// the cloud worker replaces QwenTtsWorker.exe (the original is kept as QwenTtsWorker.orig.exe)
    /// and its SHA-256 goes into runtime-manifest.json, which the game verifies; Unity Doorstop
    /// loads the in-game settings card from the SwyfCloudTts folder; a worker copy in the user
    /// profile keeps the card's Test button working after Steam restores the original, and a copy
    /// of this setup there serves the "Apps &amp; features" entry.
    internal sealed class Installer
    {
        public const string RuntimeRelative = @"Scam With Your Friends_Data\StreamingAssets\kolkata-qwen-runtime";

        private const string ProductName = "SWYF Cloud TTS";
        private const string GameProcess = "Scam With Your Friends";
        private const string WorkerName = "QwenTtsWorker.exe";
        private const string WorkerProcess = "QwenTtsWorker";
        private const string BackupName = "QwenTtsWorker.orig.exe";
        private const string ManifestName = "runtime-manifest.json";
        private const string ManifestBackupSuffix = ".swyf-backup";
        private const string ConfigName = "cloud-tts.config.json";
        private const string PluginFolderName = "SwyfCloudTts";
        private const string BootName = "SwyfCloudTts.Boot.dll";
        private const string PluginName = "SwyfCloudTts.Plugin.dll";
        private const string LoaderName = "winhttp.dll";
        private const string LoaderVersionName = ".doorstop_version";
        private const string LoaderConfigName = "doorstop_config.ini";
        private const string SetupName = "SWYF-CloudTTS-Setup.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SWYF-CloudTTS";

        private const string LoaderConfig =
            "# Unity Doorstop, written by SWYF Cloud TTS\r\n" +
            "[General]\r\n" +
            "enabled = true\r\n" +
            "target_assembly = SwyfCloudTts\\SwyfCloudTts.Boot.dll\r\n" +
            "redirect_output_log = false\r\n" +
            "boot_config_override =\r\n" +
            "ignore_disable_switch = false\r\n" +
            "\r\n" +
            "[UnityMono]\r\n" +
            "dll_search_path_override =\r\n" +
            "debug_enabled = false\r\n" +
            "debug_address = 127.0.0.1:10000\r\n" +
            "debug_suspend = false\r\n";

        /// The worker's entry in runtime-manifest.json. Game 0.1.83 writes "path" first, 0.1.95 "sha256".
        private static readonly Regex WorkerEntry = new Regex(
            "\"path\"\\s*:\\s*\"" + Regex.Escape(WorkerName) + "\"\\s*,\\s*\"sha256\"\\s*:\\s*\"(?<hash>[0-9a-fA-F]{64})\"" +
            "|\"sha256\"\\s*:\\s*\"(?<hash>[0-9a-fA-F]{64})\"\\s*,\\s*\"path\"\\s*:\\s*\"" + Regex.Escape(WorkerName) + "\"");

        private static readonly Regex OurLoaderTarget = new Regex(
            @"target_assembly\s*=\s*SwyfCloudTts\\", RegexOptions.IgnoreCase);

        private readonly string _game;
        private readonly string _state;
        private readonly bool _integrate;
        private readonly Action<string> _report;

        /// <param name="integrate">Register in "Apps &amp; features"; off for test runs with a custom state folder.</param>
        public Installer(string game, string stateDirectory, bool integrate, Action<string> report)
        {
            _game = game;
            _state = stateDirectory;
            _integrate = integrate;
            _report = report ?? (_ => { });
        }

        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        /// Folders the setup cannot delete while it runs from inside them; removed after exit.
        public static List<string> DeleteOnExit { get; } = new List<string>();

        private static string SelfPath => Assembly.GetEntryAssembly()?.Location ?? "";

        private string Runtime => Path.Combine(_game, RuntimeRelative);

        private string BinDirectory => Path.Combine(_state, "bin");

        public static InstallStatus Probe(string game)
        {
            var status = new InstallStatus();
            if (!GameLocator.IsGameDir(game))
            {
                status.State = InstallState.GameNotFound;
                return status;
            }

            string runtime = Path.Combine(game, RuntimeRelative);
            string worker = Path.Combine(runtime, WorkerName);
            if (IsOurWorker(worker))
            {
                status.InstalledVersion = WorkerVersion(worker);
                if (ManifestHash(Path.Combine(runtime, ManifestName)) != Sha256File(worker))
                {
                    status.State = InstallState.NeedsRepair;
                }
                else
                {
                    status.State = CompareVersions(status.InstalledVersion, Version) < 0
                        ? InstallState.Outdated
                        : InstallState.Installed;
                }
            }
            else
            {
                // A backup next to an original worker means Steam replaced ours during an update.
                status.State = File.Exists(Path.Combine(runtime, BackupName))
                    ? InstallState.NeedsRepair
                    : InstallState.NotInstalled;
            }

            status.CardInstalled = File.Exists(Path.Combine(game, PluginFolderName, PluginName)) &&
                                   IsOurLoaderConfig(Path.Combine(game, LoaderConfigName));
            status.CardBlocked = !status.CardInstalled && IsForeignLoader(game);
            return status;
        }

        public static bool CanWrite(string game)
        {
            foreach (string directory in new[] { game, Path.Combine(game, RuntimeRelative) })
            {
                string probe = Path.Combine(directory, ".swyf-write-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsRegistered()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                return key != null;
            }
        }

        public static string RegisteredGameDir()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                return key?.GetValue("InstallLocation") as string;
            }
        }

        public void Install()
        {
            PrepareGameFolder();

            byte[] worker = Payload.Read(WorkerName);
            string workerPath = Path.Combine(Runtime, WorkerName);
            string backupPath = Path.Combine(Runtime, BackupName);
            string manifestPath = Path.Combine(Runtime, ManifestName);

            if (!IsOurWorker(workerPath))
            {
                Report(Strings.StepBackup);
                File.Copy(workerPath, backupPath, true);
                File.Copy(manifestPath, manifestPath + ManifestBackupSuffix, true);
            }
            else if (!File.Exists(backupPath))
            {
                Report(Strings.WarnNoBackup);
            }

            Report(Strings.StepWorker);
            File.WriteAllBytes(workerPath, worker);

            Report(Strings.StepManifest);
            PatchManifest(manifestPath, Sha256(worker));

            InstallCard();

            Report(Strings.StepAppFiles);
            Directory.CreateDirectory(BinDirectory);
            File.WriteAllBytes(Path.Combine(BinDirectory, WorkerName), worker);
            MigrateConfig();
            RemoveLegacyPanel();
            if (_integrate)
            {
                Register();
            }

            SetupLog.Write($"installed {Version} into {_game}");
        }

        public void Uninstall(bool removeSettings)
        {
            if (GameLocator.IsGameDir(_game))
            {
                PrepareGameFolder();
                RestoreWorker();
                RemoveCard();
                MigrateConfig();
            }

            Report(Strings.StepCleanup);
            RemoveLegacyPanel();
            if (_integrate)
            {
                Unregister();
            }

            RemoveProfileFiles(removeSettings);
            SetupLog.Write($"uninstalled from {_game} (settings removed: {removeSettings})");
        }

        /// Removes folders the setup could not delete while it was running from them.
        public static void ScheduleDeleteOnExit()
        {
            if (DeleteOnExit.Count == 0)
            {
                return;
            }

            var command = new StringBuilder("/c ping -n 3 127.0.0.1 >nul");
            foreach (string directory in DeleteOnExit)
            {
                command.Append(" & rmdir /s /q \"").Append(directory).Append('"');
            }

            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", command.ToString())
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex)
            {
                SetupLog.Write("cannot schedule cleanup: " + ex.Message);
            }
        }

        private void Report(string message)
        {
            SetupLog.Write(message);
            _report(message);
        }

        private void PrepareGameFolder()
        {
            if (Process.GetProcessesByName(GameProcess).Length > 0)
            {
                throw new SetupException(Strings.GameRunning);
            }

            StopOwnWorkers();

            foreach (string required in new[] { Path.Combine(Runtime, WorkerName), Path.Combine(Runtime, ManifestName) })
            {
                if (!File.Exists(required))
                {
                    throw new SetupException(required + " is missing. Verify the game files in Steam and try again.");
                }
            }
        }

        /// A worker left running by a crashed game would lock the files about to be replaced.
        private void StopOwnWorkers()
        {
            foreach (Process process in Process.GetProcessesByName(WorkerProcess))
            {
                try
                {
                    string path = process.MainModule?.FileName ?? "";
                    if (IsInside(path, Runtime) || IsInside(path, _state))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
                catch (Exception ex)
                {
                    SetupLog.Write($"cannot stop worker {process.Id}: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private void RestoreWorker()
        {
            string workerPath = Path.Combine(Runtime, WorkerName);
            string backupPath = Path.Combine(Runtime, BackupName);
            string manifestPath = Path.Combine(Runtime, ManifestName);
            string manifestBackup = manifestPath + ManifestBackupSuffix;

            if (IsOurWorker(workerPath))
            {
                if (!File.Exists(backupPath))
                {
                    Report(Strings.WarnRestoreWithSteam);
                    return;
                }

                Report(Strings.StepRestore);
                File.Copy(backupPath, workerPath, true);
                try
                {
                    PatchManifest(manifestPath, Sha256File(workerPath));
                }
                catch (SetupException) when (File.Exists(manifestBackup))
                {
                    File.Copy(manifestBackup, manifestPath, true);
                }
            }

            DeleteFile(backupPath);
            DeleteFile(manifestBackup);
        }

        private void InstallCard()
        {
            byte[] loader = Payload.Read(LoaderName);
            if (IsForeignLoader(_game))
            {
                Report(Strings.CardBlocked);
                return;
            }

            Report(Strings.StepCard);
            File.WriteAllBytes(Path.Combine(_game, LoaderName), loader);
            File.WriteAllBytes(Path.Combine(_game, LoaderVersionName), Payload.Read(LoaderVersionName));
            File.WriteAllText(Path.Combine(_game, LoaderConfigName), LoaderConfig, new UTF8Encoding(false));

            string folder = Path.Combine(_game, PluginFolderName);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, BootName), Payload.Read(BootName));
            File.WriteAllBytes(Path.Combine(folder, PluginName), Payload.Read(PluginName));
            foreach (string stale in Directory.GetFiles(folder, "*.pdb"))
            {
                DeleteFile(stale);
            }
        }

        private void RemoveCard()
        {
            string config = Path.Combine(_game, LoaderConfigName);
            if (IsOurLoaderConfig(config))
            {
                Report(Strings.StepRemoveCard);
                DeleteFile(Path.Combine(_game, LoaderName));
                DeleteFile(Path.Combine(_game, LoaderVersionName));
                DeleteFile(config);
                DeleteFile(config + ".bak-swyf");
            }

            DeleteDirectory(Path.Combine(_game, PluginFolderName));
        }

        /// Versions before 1.2 kept the config, API keys included, inside the game folder.
        private void MigrateConfig()
        {
            string legacy = Path.Combine(Runtime, ConfigName);
            if (!File.Exists(legacy))
            {
                return;
            }

            string user = Path.Combine(_state, ConfigName);
            if (!File.Exists(user))
            {
                Directory.CreateDirectory(_state);
                File.Copy(legacy, user);
            }

            DeleteFile(legacy);
        }

        /// Versions before 1.3 had a browser settings panel with its own copy and shortcut.
        private void RemoveLegacyPanel()
        {
            DeleteDirectory(Path.Combine(_state, "panel"));
            DeleteFile(Path.Combine(_state, "panel.token"));
            if (_integrate)
            {
                DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SWYF Cloud TTS.lnk"));
            }
        }

        private void RemoveProfileFiles(bool removeSettings)
        {
            DeleteDirectory(Path.Combine(_state, "cache"));

            string target = removeSettings ? _state : BinDirectory;
            if (!Directory.Exists(target))
            {
                return;
            }

            if (!IsInside(SelfPath, target))
            {
                DeleteDirectory(target);
                return;
            }

            foreach (string file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(SelfPath), StringComparison.OrdinalIgnoreCase))
                {
                    DeleteFile(file);
                }
            }

            DeleteOnExit.Add(target);
        }

        private void Register()
        {
            string setupCopy = Path.Combine(BinDirectory, SetupName);
            if (!string.Equals(Path.GetFullPath(SelfPath), Path.GetFullPath(setupCopy), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(SelfPath, setupCopy, true);
            }

            long bytes = 0;
            foreach (string file in Directory.GetFiles(BinDirectory))
            {
                bytes += new FileInfo(file).Length;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", ProductName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "kyripkaraw");
                key.SetValue("DisplayIcon", setupCopy);
                key.SetValue("InstallLocation", _game);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("ModifyPath", Quote(setupCopy));
                key.SetValue("UninstallString", Quote(setupCopy) + " --uninstall");
                key.SetValue("QuietUninstallString", Quote(setupCopy) + " --uninstall --quiet");
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }
        }

        private static void Unregister()
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        }

        private static bool IsOurWorker(string path)
        {
            try
            {
                return File.Exists(path) && FileVersionInfo.GetVersionInfo(path).ProductName == ProductName;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string WorkerVersion(string path)
        {
            string raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return System.Version.TryParse(raw, out Version parsed) ? parsed.ToString(3) : raw ?? "";
        }

        private static int CompareVersions(string left, string right)
        {
            System.Version.TryParse(left, out Version a);
            System.Version.TryParse(right, out Version b);
            return (a ?? new Version(0, 0)).CompareTo(b ?? new Version(0, 0));
        }

        private static bool IsOurLoaderConfig(string path)
        {
            try
            {
                return File.Exists(path) && OurLoaderTarget.IsMatch(File.ReadAllText(path));
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// Another mod loader (BepInEx, MelonLoader...) owns winhttp.dll; replacing it would break that mod.
        private static bool IsForeignLoader(string game)
        {
            string config = Path.Combine(game, LoaderConfigName);
            if (File.Exists(config))
            {
                return !IsOurLoaderConfig(config);
            }

            string loader = Path.Combine(game, LoaderName);
            return File.Exists(loader) && Sha256File(loader) != Sha256(Payload.Read(LoaderName));
        }

        private static void PatchManifest(string manifest, string hash)
        {
            string text = File.ReadAllText(manifest);
            Match match = WorkerEntry.Match(text);
            if (!match.Success)
            {
                throw new SetupException(Strings.ManifestChanged);
            }

            Group old = match.Groups["hash"];
            string updated = text.Substring(0, old.Index) + hash + text.Substring(old.Index + old.Length);
            File.WriteAllText(manifest, updated, new UTF8Encoding(false));
        }

        private static string ManifestHash(string manifest)
        {
            try
            {
                Match match = WorkerEntry.Match(File.ReadAllText(manifest));
                return match.Success ? match.Groups["hash"].Value.ToLowerInvariant() : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(data));
            }
        }

        private static string Sha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return Hex(sha.ComputeHash(stream));
            }
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }

        private static bool IsInside(string path, string directory)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
            {
                return false;
            }

            string root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string path) => "\"" + path + "\"";

        private static void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    /// Files embedded into the setup at build time.
    internal static class Payload
    {
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();

        public static byte[] Read(string name)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(name, out byte[] cached))
                {
                    return cached;
                }

                using (Stream stream = typeof(Payload).Assembly.GetManifestResourceStream("payload/" + name))
                {
                    if (stream == null)
                    {
                        throw new SetupException(string.Format(Strings.DamagedSetup, name));
                    }

                    var buffer = new MemoryStream((int)stream.Length);
                    stream.CopyTo(buffer);
                    byte[] data = buffer.ToArray();
                    Cache[name] = data;
                    return data;
                }
            }
        }
    }
}
