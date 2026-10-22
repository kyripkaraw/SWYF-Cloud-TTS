using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SwyfCloudTts.Plugin
{
    internal sealed class CloudTtsTestResult
    {
        public bool Ok;
        public byte[] Wav;
        public string Message = "";
        public string StandardOutput = "";
        public string StandardError = "";
        public int ExitCode = -1;
    }

    /// <summary>
    /// Runs the worker in one of its one-shot modes (--test, --list-voices) with the values of the
    /// form. The API key goes into a temporary config file, never onto the command line, and the
    /// file is deleted afterwards.
    /// </summary>
    internal static class CloudTtsTester
    {
        private const int TimeoutMs = 120000;

        public static Task<CloudTtsTestResult> RunAsync(
            string configJson, string text, string language, string workerPath, CancellationToken token)
        {
            return Task.Run(() => Run(configJson, workerPath, token, (directory, args) =>
            {
                string outputPath = Path.Combine(directory, "test.wav");
                string safeText = OneLine(text, 500);
                if (safeText.Length == 0)
                {
                    safeText = CloudTtsText.T("test.default");
                }

                string safeLanguage = OneLine(language, 60);
                args.Append("--test --out ").Append(Quote(outputPath))
                    .Append(" --text ").Append(Quote(safeText))
                    .Append(" --language ").Append(Quote(safeLanguage.Length > 0 ? safeLanguage : "English"));
                return result => ReadWav(outputPath, result);
            }), token);
        }

        /// <summary>Asks the worker to save the account's own voices to elevenlabs-voices.json.</summary>
        public static Task<CloudTtsTestResult> ListVoicesAsync(string configJson, string workerPath, CancellationToken token)
        {
            return Task.Run(() => Run(configJson, workerPath, token, (directory, args) =>
            {
                args.Append("--list-voices");
                return result => result.Ok = true;
            }), token);
        }

        private static CloudTtsTestResult Run(
            string configJson, string workerPath, CancellationToken token,
            Func<string, StringBuilder, Action<CloudTtsTestResult>> prepare)
        {
            var result = new CloudTtsTestResult();
            string tempDirectory = Path.Combine(
                Path.GetTempPath(), "SWYF-CloudTTS", "plugin-run-" + Guid.NewGuid().ToString("N"));

            try
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(workerPath) || !File.Exists(workerPath))
                {
                    result.Message = CloudTtsText.T("worker.missing");
                    return result;
                }

                Directory.CreateDirectory(tempDirectory);
                string configPath = Path.Combine(tempDirectory, "cloud-tts.config.json");
                File.WriteAllText(configPath, configJson, new UTF8Encoding(false));

                var args = new StringBuilder();
                Action<CloudTtsTestResult> finish = prepare(tempDirectory, args);
                args.Append(" --config ").Append(Quote(configPath));

                var startInfo = new ProcessStartInfo
                {
                    FileName = workerPath,
                    Arguments = args.ToString(),
                    WorkingDirectory = Path.GetDirectoryName(workerPath) ?? "",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    try
                    {
                        process.Start();
                    }
                    catch (Exception ex)
                    {
                        result.Message = "could not start QwenTtsWorker.exe: " + ex.Message;
                        return result;
                    }

                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                    bool exited = process.WaitForExit(TimeoutMs);
                    if (!exited)
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(5000); } catch { }
                        result.Message = CloudTtsText.T("worker.timeout");
                    }

                    try { process.WaitForExit(); } catch { }

                    result.StandardOutput = SafeResult(stdoutTask);
                    result.StandardError = SafeResult(stderrTask);
                    result.ExitCode = exited ? process.ExitCode : -1;
                }

                if (result.Message.Length > 0)
                {
                    return result;
                }

                if (result.ExitCode != 0)
                {
                    string error = FirstLine(result.StandardError);
                    if (error.Length == 0)
                    {
                        error = FirstLine(result.StandardOutput);
                    }

                    result.Message = error.Length > 0 ? error : "QwenTtsWorker.exe exited with code " + result.ExitCode;
                    return result;
                }

                finish(result);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Message = CloudTtsText.T("test.cancelled");
                return result;
            }
            catch (Exception ex)
            {
                result.Message = ex.Message;
                return result;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Debug("cannot delete the temporary directory: " + ex.Message);
                }
            }
        }

        private static void ReadWav(string path, CloudTtsTestResult result)
        {
            if (!File.Exists(path))
            {
                result.Message = "the worker did not produce a WAV file";
                return;
            }

            byte[] wav = File.ReadAllBytes(path);
            if (wav.Length < 44)
            {
                result.Message = "the worker produced an empty WAV file";
                return;
            }

            result.Wav = wav;
            result.Ok = true;
        }

        private static string OneLine(string text, int max)
        {
            string line = (text ?? "").Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ').Trim();
            return line.Length > max ? line.Substring(0, max) : line;
        }

        private static string SafeResult(Task<string> task)
        {
            try
            {
                return task.GetAwaiter().GetResult() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length > 0)
                    {
                        return line.Length <= 500 ? line : line.Substring(0, 500);
                    }
                }
            }

            return "";
        }

        /// <summary>
        /// Windows argument quoting: backslashes are literal except right before a quote, so
        /// only a trailing run is doubled to keep it from escaping the closing quote.
        /// </summary>
        private static string Quote(string value)
        {
            string text = (value ?? "").Replace("\"", "'");
            int trailing = 0;
            while (trailing < text.Length && text[text.Length - 1 - trailing] == '\\')
            {
                trailing++;
            }

            return "\"" + text + new string('\\', trailing) + "\"";
        }
    }
}
