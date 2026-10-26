using System.Text;

namespace SwyfCloudTts.Plugin
{
    internal sealed class CloudTtsTestResult
    {
        public bool Ok;
        public byte[] Wav;
        public string Message = "";
    }

    /// The BepInEx build has no worker process, so Test voice and Load my voices run the engine
    /// in process. The form's values are written to a temporary config, which carries the API
    /// key only for this run and is deleted afterwards; the worker path is not used.
    internal static class CloudTtsTester
    {
        public static Task<CloudTtsTestResult> RunAsync(
            string configJson, string text, string language, string workerPath, CancellationToken token)
        {
            return Task.Run(() => Run(configJson, token, config =>
            {
                var result = new CloudTtsTestResult();
                string phrase = OneLine(text, 500);
                if (phrase.Length == 0)
                {
                    phrase = CloudTtsText.T("test.default");
                }

                string callLanguage = OneLine(language, 60);
                float[] samples = TestRunner.Synthesize(config, phrase, callLanguage.Length > 0 ? callLanguage : "English",
                    null, line => Log.Info("test: " + line), out string error, out _);
                if (samples == null)
                {
                    result.Message = error;
                    return result;
                }

                result.Wav = Pcm.ToWav(samples);
                result.Ok = true;
                return result;
            }), token);
        }

        /// Saves the account's own ElevenLabs voices to elevenlabs-voices.json for the card.
        public static Task<CloudTtsTestResult> ListVoicesAsync(string configJson, string workerPath, CancellationToken token)
        {
            return Task.Run(() => Run(configJson, token, config =>
            {
                var result = new CloudTtsTestResult();
                if (string.IsNullOrWhiteSpace(config.ElevenLabs.ApiKey))
                {
                    result.Message = "no ElevenLabs API key";
                    return result;
                }

                using (var provider = new Providers.ElevenLabsTts(config, new VoiceResolver(config)))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    int count = provider.SaveAccountVoicesAsync(timeout.Token).GetAwaiter().GetResult();
                    Log.Info($"saved {count} ElevenLabs voice(s) for the settings card");
                }

                result.Ok = true;
                return result;
            }), token);
        }

        private static CloudTtsTestResult Run(string configJson, CancellationToken token, Func<Config, CloudTtsTestResult> body)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SWYF-CloudTTS", "plugin-run-" + Guid.NewGuid().ToString("N"));
            try
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, Config.FileName);
                File.WriteAllText(path, configJson, new UTF8Encoding(false));
                return body(Config.Load(path));
            }
            catch (OperationCanceledException)
            {
                return new CloudTtsTestResult { Message = CloudTtsText.T("test.cancelled") };
            }
            catch (Exception ex)
            {
                return new CloudTtsTestResult { Message = ex.Message };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Debug("cannot delete the temporary directory: " + ex.Message);
                }
            }
        }

        private static string OneLine(string text, int max)
        {
            string line = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return line.Length > max ? line.Substring(0, max) : line;
        }
    }
}
