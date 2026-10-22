using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SwyfCloudTts.Setup
{
    /// <summary>Finds the game through a previous install or the Steam library list.</summary>
    internal static class GameLocator
    {
        private const string SteamFolderName = "Scam With Your Friends";

        public static bool IsGameDir(string directory)
            => !string.IsNullOrWhiteSpace(directory) &&
               Directory.Exists(Path.Combine(directory, Installer.RuntimeRelative));

        /// <summary>Accepts the game folder or any folder inside it, as people often pick a subfolder.</summary>
        public static string Normalize(string path)
        {
            string current = path;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(current); i++)
            {
                if (IsGameDir(current))
                {
                    return Path.GetFullPath(current);
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        public static string Find()
        {
            string previous = Installer.RegisteredGameDir();
            if (IsGameDir(previous))
            {
                return previous;
            }

            foreach (string library in SteamLibraries())
            {
                string candidate = Path.Combine(library, "steamapps", "common", SteamFolderName);
                if (IsGameDir(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string steam = SteamPath();
            if (steam == null)
            {
                yield break;
            }

            seen.Add(steam);
            yield return steam;

            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                string library = match.Groups[1].Value.Replace(@"\\", @"\");
                if (seen.Add(library))
                {
                    yield return library;
                }
            }
        }

        private static string SteamPath()
        {
            string[][] keys =
            {
                new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath" },
                new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath" },
                new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath" },
            };

            foreach (string[] key in keys)
            {
                try
                {
                    if (Registry.GetValue(key[0], key[1], null) is string value && value.Length > 0)
                    {
                        return value.Replace('/', '\\');
                    }
                }
                catch (Exception)
                {
                    // Try the next registry location.
                }
            }

            return null;
        }
    }
}
