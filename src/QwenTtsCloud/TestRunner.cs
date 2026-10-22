using System.Diagnostics;
using SwyfCloudTts.Providers;

namespace SwyfCloudTts;

/// <summary>
/// "QwenTtsWorker.exe --test": synthesizes one phrase into a WAV file. The in-game settings card
/// uses it for the Test button. It never writes to the audio cache, so it is safe while the
/// game is running. ElevenLabs only clones here when autoClone is on and a real game clip exists.
/// </summary>
internal static class TestRunner
{
    public static int Run(string[] args)
    {
        string text = Cli.Value(args, "--text") is { Length: > 0 } custom
            ? custom
            : "Привет! Это проверка облачного голоса.";

        if (text.Length > 500)
        {
            text = text[..500];
        }

        string outputPath = Cli.Value(args, "--out") is { Length: > 0 } outArg
            ? outArg
            : "cloud-tts-test.wav";
        string language = Cli.Value(args, "--language") is { Length: > 0 } lang ? lang : "Russian";

        Config config;
        try
        {
            config = Config.Load(Cli.Value(args, "--config"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cannot load config: " + ex.Message);
            return 2;
        }

        Log.Init(config.StateDirectory, verbose: true);

        string? provider = Cli.Value(args, "--provider");
        if (!string.IsNullOrWhiteSpace(provider))
        {
            config.Provider = provider!.Trim().ToLowerInvariant();
        }

        string? baseUrl = Cli.Value(args, "--base-url");
        if (!string.IsNullOrWhiteSpace(baseUrl) && config.Provider == "qwenremote")
        {
            config.QwenRemote.BaseUrl = baseUrl!;
        }

        string? key = Cli.Value(args, "--key");
        if (!string.IsNullOrWhiteSpace(key))
        {
            switch (config.Provider)
            {
                case "openai": config.OpenAi.ApiKey = key!; break;
                case "elevenlabs": config.ElevenLabs.ApiKey = key!; break;
                case "googleai": config.GoogleAi.ApiKey = key!; break;
                case "qwenremote": config.QwenRemote.ApiKey = key!; break;
            }
        }

        string? model = Cli.Value(args, "--model");
        if (!string.IsNullOrWhiteSpace(model))
        {
            switch (config.Provider)
            {
                case "openai": config.OpenAi.Model = model!; break;
                case "elevenlabs": config.ElevenLabs.ModelId = model!; break;
                case "googleai": config.GoogleAi.Model = model!; break;
                case "qwenremote": config.QwenRemote.Model = model!; break;
            }
        }

        string? mode = Cli.Value(args, "--mode");
        if (!string.IsNullOrWhiteSpace(mode) && config.Provider == "qwenremote")
        {
            string value = mode!.Trim().ToLowerInvariant();
            config.QwenRemote.Mode = value switch
            {
                "voice" => "voice",
                "design" => "design",
                _ => "clone",
            };
        }

        string? voice = Cli.Value(args, "--voice");
        if (!string.IsNullOrWhiteSpace(voice))
        {
            switch (config.Provider)
            {
                case "openai": config.OpenAi.Voice = voice; break;
                case "elevenlabs":
                    config.ElevenLabs.DefaultVoiceId = voice!;
                    config.ElevenLabs.AutoClone = false;
                    break;
                case "googleai": config.GoogleAi.VoiceId = voice!; break;
                case "qwenremote": config.QwenRemote.Voice = voice!; break;
            }
        }

        // Cloning needs a real game clip; previews without one use a stock or server voice.
        float[] reference = Array.Empty<float>();
        string clipSpeaker = "";
        if (config.Provider == "elevenlabs")
        {
            (reference, clipSpeaker) = TryLoadReference(config.StateDirectory, Cli.Value(args, "--speaker"));
            if (reference.Length == 0)
            {
                config.ElevenLabs.AutoClone = false;
                Console.WriteLine("reference: none found in the voice cache, previewing the stock voice");
            }
            else
            {
                Console.WriteLine($"reference: {reference.Length / (double)Pcm.SampleRate:0.0} s " +
                                  "from the voice cache (the clone path will be exercised)");
            }
        }

        else if (config.Provider == "qwenremote" && config.QwenRemote.Mode == "clone")
        {
            (reference, clipSpeaker) = TryLoadReference(config.StateDirectory, Cli.Value(args, "--speaker"));
            if (reference.Length == 0)
            {
                config.QwenRemote.Mode = "voice";
                Console.WriteLine("reference: none found in the voice cache, previewing the server voice");
            }
            else
            {
                Console.WriteLine($"reference: {reference.Length / (double)Pcm.SampleRate:0.0} s from the voice cache");
            }
        }

        Console.WriteLine($"config:   {config.Path}");
        Console.WriteLine($"provider: {config.Provider}");
        Console.WriteLine($"text:     {text}");

        var voices = new VoiceResolver(config);

        // A saved game clip belongs to a known character, so the test uses (or reuses) their voice.
        VoiceEntry speaker = voices.Find(clipSpeaker) ??
                             (reference.Length > 0
                                 ? voices.Resolve(reference, language)
                                 : new VoiceEntry { Id = "preview", Gender = "male" });
        using ITtsProvider tts = ProviderFactory.Create(config, voices, out bool passthroughOnly);

        if (passthroughOnly)
        {
            Console.Error.WriteLine("provider 'passthrough' runs the local model; nothing to test");
            return 2;
        }

        if (!tts.Ready)
        {
            Console.Error.WriteLine($"provider '{tts.Name}' is not configured.");
            Console.Error.WriteLine("Use --key/--base-url, edit the config, or set SWYF_TTS_GOOGLEAI_KEY / " +
                                    "SWYF_TTS_ELEVENLABS_KEY / SWYF_TTS_OPENAI_KEY / " +
                                    "SWYF_TTS_QWENREMOTE_URL.");
            return 2;
        }

        var samples = new List<float>(Pcm.SampleRate * 8);
        int cap = Pcm.SampleRate * 30;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            tts.SpeakAsync(
                new SpeechRequest(text, language, reference,
                    speaker),
                chunk =>
                {
                    if (samples.Count < cap)
                    {
                        samples.AddRange(chunk);
                    }
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Console.Error.WriteLine($"FAILED after {stopwatch.ElapsedMilliseconds} ms: {ex.Message}");
            return 3;
        }

        stopwatch.Stop();

        if (samples.Count == 0)
        {
            Console.Error.WriteLine("provider returned no audio");
            return 4;
        }

        try
        {
            string full = Path.GetFullPath(outputPath);
            File.WriteAllBytes(full, Pcm.ToWav(samples.ToArray()));
            Console.WriteLine(
                $"ok: {samples.Count} samples ({samples.Count / (double)Pcm.SampleRate:0.00} s) " +
                $"in {stopwatch.ElapsedMilliseconds} ms");
            Console.WriteLine($"wav: {full}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cannot write wav: " + ex.Message);
            return 5;
        }

        return 0;
    }

    /// <summary>A clip the game has already sent (the given speaker's, or the first one), to test cloning.</summary>
    private static (float[] Samples, string Speaker) TryLoadReference(string stateDirectory, string? speakerId)
    {
        try
        {
            string directory = Path.Combine(stateDirectory, "voices");
            string? file = !string.IsNullOrWhiteSpace(speakerId)
                ? Path.Combine(directory, speakerId.Trim() + ".wav")
                : Directory.Exists(directory)
                    ? Directory.GetFiles(directory, "*.wav").OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault()
                    : null;

            if (file == null || !File.Exists(file))
            {
                return (Array.Empty<float>(), "");
            }

            float[] samples = Pcm.FromWav(File.ReadAllBytes(file));
            int cap = Pcm.SampleRate * 10;
            return (samples.Length > cap ? samples[..cap] : samples, Path.GetFileNameWithoutExtension(file));
        }
        catch (Exception ex)
        {
            Console.WriteLine("reference: cannot read a cached clip (" + ex.Message + ")");
            return (Array.Empty<float>(), "");
        }
    }
}
