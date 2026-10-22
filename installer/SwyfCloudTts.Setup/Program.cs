using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace SwyfCloudTts.Setup
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Options options = Options.Parse(args);
            Strings.Init(options.Language);
            SetupLog.Init(options.StateDirectory);
            SetupLog.Write($"setup {Installer.Version} started: {string.Join(" ", args)}");

            int exitCode = 0;
            if (options.Quiet)
            {
                exitCode = RunQuiet(options);
            }
            else
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupForm(options));
            }

            Installer.ScheduleDeleteOnExit();
            return exitCode;
        }

        /// "--install", "--uninstall" or "--status" with "--quiet": no window, exit code 0 on success.
        private static int RunQuiet(Options options)
        {
            AttachConsole(-1);
            Console.OutputEncoding = new UTF8Encoding(false);
            string game = options.Game != null ? GameLocator.Normalize(options.Game) : GameLocator.Find();
            var installer = new Installer(game, options.StateDirectory, options.Integrate, Console.WriteLine);

            try
            {
                if (options.Status)
                {
                    InstallStatus status = Installer.Probe(game);
                    Console.WriteLine($"game: {game ?? "(not found)"}");
                    Console.WriteLine($"state: {status.State} {status.InstalledVersion}".TrimEnd());
                    Console.WriteLine($"card: {(status.CardInstalled ? "installed" : status.CardBlocked ? "blocked by another loader" : "missing")}");
                    Console.WriteLine($"setup: {Installer.Version}");
                }
                else if (options.Uninstall)
                {
                    installer.Uninstall(options.Purge);
                    Console.WriteLine(Strings.DoneUninstall);
                }
                else
                {
                    if (game == null)
                    {
                        throw new SetupException(Strings.StatusGameNotFound);
                    }

                    installer.Install();
                    Console.WriteLine(Strings.DoneInstall);
                }

                return 0;
            }
            catch (Exception ex)
            {
                SetupLog.Write(ex.ToString());
                Console.Error.WriteLine(ex is SetupException ? ex.Message : string.Format(Strings.Failed, ex.Message));
                return 1;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);
    }

    internal sealed class Options
    {
        public bool Install;
        public bool Uninstall;
        public bool Run;
        public bool Quiet;
        public bool Purge;
        public bool Status;
        public string Game;
        public string Language;
        public string StateDirectory = DefaultStateDirectory;

        /// A custom state folder is for test runs, so it never touches the registry.
        public bool Integrate = true;

        public static string DefaultStateDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWYF-CloudTTS");

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i].ToLowerInvariant())
                {
                    case "--install":
                        options.Install = true;
                        break;
                    case "--uninstall":
                        options.Uninstall = true;
                        break;
                    case "--run":
                        options.Run = true;
                        break;
                    case "--quiet":
                        options.Quiet = true;
                        break;
                    case "--purge":
                        options.Purge = true;
                        break;
                    case "--status":
                        options.Status = true;
                        options.Quiet = true;
                        break;
                    case "--game":
                        options.Game = next;
                        i++;
                        break;
                    case "--lang":
                        options.Language = next;
                        i++;
                        break;
                    case "--state-dir":
                        options.StateDirectory = Path.GetFullPath(next ?? ".");
                        options.Integrate = false;
                        i++;
                        break;
                }
            }

            return options;
        }

        /// Arguments for the elevated copy, which starts the chosen action right away.
        public string ElevatedArguments(bool install, string game, bool purge)
        {
            var parts = new List<string> { install ? "--install" : "--uninstall", "--run", "--game", Quote(game), "--lang", Strings.Code };
            if (purge)
            {
                parts.Add("--purge");
            }

            if (!Integrate)
            {
                parts.Add("--state-dir");
                parts.Add(Quote(StateDirectory));
            }

            return string.Join(" ", parts);
        }

        private static string Quote(string value) => "\"" + value.TrimEnd('\\') + "\"";
    }

    internal static class Elevation
    {
        public static bool IsElevated
        {
            get
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
        }

        /// Returns false when the user declines the UAC prompt.
        public static bool Relaunch(string arguments)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location, arguments)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                return true;
            }
            catch (Win32Exception)
            {
                return false;
            }
        }
    }

    /// Plain-text log next to the settings, attached to bug reports.
    internal static class SetupLog
    {
        private static string _path;

        public static string Path => _path ?? "";

        public static void Init(string stateDirectory)
        {
            try
            {
                Directory.CreateDirectory(stateDirectory);
                _path = System.IO.Path.Combine(stateDirectory, "setup.log");
            }
            catch (Exception)
            {
                _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SWYF-CloudTTS-setup.log");
            }
        }

        public static void Write(string message)
        {
            if (_path == null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // The log is a convenience; setup must not fail because of it.
            }
        }
    }
}
