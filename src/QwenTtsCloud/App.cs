using System.Diagnostics;
using SwyfCloudTts.Providers;

namespace SwyfCloudTts;

/// Protocol implementation. Mirrors the original worker's validation exactly, then proxies
/// speech generation to a cloud provider while streaming results back in the same format.
internal sealed class App : IDisposable
{
    private const int MaxSamples = 720000;

    /// 50 ms of silence, enough to satisfy "produced some speech" checks.
    private const int SilencePadSamples = 1200;

    /// The game gives up on the worker for the whole session when the first audio of a line takes
    /// more than 20 s, so a line still silent after this budget is answered with silence instead.
    private static readonly TimeSpan FirstAudioBudget = TimeSpan.FromSeconds(15);

    /// The local model needs most of the game's 20 s just to load.
    private static readonly TimeSpan PassthroughCutoff = TimeSpan.FromSeconds(5);

    private readonly Config _config;
    private readonly VoiceResolver _voices;
    private readonly AudioCache _cache;
    private readonly ITtsProvider _provider;
    private readonly PassthroughClient? _passthrough;
    private readonly bool _passthroughOnly;

    private bool _loaded;

    private App(Config config, ITtsProvider provider, bool passthroughOnly, VoiceResolver voices,
                AudioCache cache, PassthroughClient? passthrough)
    {
        _config = config;
        _provider = provider;
        _passthroughOnly = passthroughOnly;
        _voices = voices;
        _cache = cache;
        _passthrough = passthrough;
    }

    public static App Create()
    {
        Config config = Config.Load();
        Log.Init(config.StateDirectory, config.Logging.Verbose);
        Log.Info($"QwenTtsCloud worker; state dir {config.StateDirectory}");
        Log.Info($"config: {config.Path}");

        var voices = new VoiceResolver(config);
        var cache = new AudioCache(config);
        PassthroughClient? passthrough = null;

        if (config.Fallback.Passthrough)
        {
            passthrough = new PassthroughClient(config);
            if (!passthrough.Available)
            {
                Log.Warn("fallback.passthrough is enabled but " + config.Fallback.OriginalExe +
                         " is missing; the installer should have kept a copy of the original worker");
                passthrough = null;
            }
        }

        string name = (config.Provider ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            name = "googleai";
        }

        ITtsProvider provider = ProviderFactory.Create(config, voices, out bool passthroughOnly);

        if (name is "passthrough" && passthrough == null)
        {
            Log.Error("provider 'passthrough' needs fallback.passthrough and QwenTtsWorker.orig.exe; emitting silence");
            passthroughOnly = false;
        }

        if (!passthroughOnly && !provider.Ready)
        {
            Log.Error(provider.Name == "qwenremote"
                ? $"provider 'qwenremote' has no valid server URL; set qwenremote.baseUrl in " +
                  $"{config.Path} or through SWYF_TTS_QWENREMOTE_URL (an optional token comes from " +
                  "SWYF_TTS_QWENREMOTE_KEY / QWEN_TTS_API_KEY)"
                : $"provider '{provider.Name}' has no credentials; set an api key in " +
                  $"{config.Path} or through SWYF_TTS_GOOGLEAI_KEY / SWYF_TTS_ELEVENLABS_KEY / " +
                  "SWYF_TTS_OPENAI_KEY");

            if (passthrough != null)
            {
                Log.Warn("switching to the local model through passthrough");
                passthroughOnly = true;
            }
            else
            {
                Log.Error("no fallback available; the game will hear silence");
            }
        }

        Log.Info($"provider={provider.Name}{(passthroughOnly && provider.Name != "passthrough" ? " (passthrough)" : "")}, " +
                 $"cache={cache.Enabled}, passthrough={passthrough != null}");

        return new App(config, provider, passthroughOnly, voices, cache, passthrough);
    }

    public void Load(BinaryReader reader, BinaryWriter writer)
    {
        string talker = reader.ReadString();
        string codec = reader.ReadString();
        string backend = reader.ReadString();

        if (_loaded || (backend != "CPU" && backend != "Vulkan0"))
        {
            throw new InvalidDataException("Invalid Qwen load request.");
        }

        _loaded = true;
        _passthrough?.RememberLoad(talker, codec, backend);

        Log.Info($"load: talker={Path.GetFileName(talker)}, codec={Path.GetFileName(codec)}, backend={backend}");

        writer.Write(1);
        writer.Write(1);
        writer.Flush();
    }

    public void Synthesize(BinaryReader reader, BinaryWriter writer)
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("Qwen is not loaded.");
        }

        string text = reader.ReadString();
        string language = reader.ReadString();

        if (text.Length == 0 || text.Length > 2000 || !SupportedLanguage(language))
        {
            throw new InvalidDataException("Invalid Qwen speech request.");
        }

        int referenceLength = reader.ReadInt32();
        if (referenceLength < 2400 || referenceLength > 240000)
        {
            throw new InvalidDataException("Invalid Qwen reference length.");
        }

        byte[] raw = reader.ReadBytes(referenceLength * 4);
        if (raw.Length != referenceLength * 4)
        {
            throw new EndOfStreamException();
        }

        float[] reference = new float[referenceLength];
        Buffer.BlockCopy(raw, 0, reference, 0, raw.Length);
        for (int i = 0; i < referenceLength; i++)
        {
            if (float.IsNaN(reference[i]) || float.IsInfinity(reference[i]))
            {
                throw new InvalidDataException("Invalid reference sample.");
            }
        }

        try
        {
            Respond(writer, text, language, reference);
        }
        catch (Exception ex)
        {
            // Never take the worker down over a single line: the game disables speech
            // for the whole session when the worker exits with an error reply.
            Log.Error("unexpected synthesis failure: " + ex);

            try
            {
                var sink = new SampleSink(writer, MaxSamples);
                sink.PadSilence(SilencePadSamples);
                sink.Flush();
                writer.Write(3);
                writer.Flush();
            }
            catch
            {
                // The game closed the pipe.
            }
        }
    }

    private void Respond(BinaryWriter writer, string text, string language, float[] reference)
    {
        var clock = Stopwatch.StartNew();
        VoiceEntry voice = _voices.Resolve(reference, language);

        string signature = _passthroughOnly ? "passthrough" : _provider.VoiceSignature(voice);
        string key = AudioCache.Key(CacheProviderName, signature, language, text);

        if (_cache.TryGet(key, out byte[] cached))
        {
            int samples = SampleSink.PumpS16(writer, cached, MaxSamples);
            writer.Write(3);
            writer.Flush();
            Log.Info($"cache hit: {text.Length} chars -> {samples} samples in {clock.ElapsedMilliseconds} ms");
            return;
        }

        Log.Debug($"synth: voice={VoiceResolver.Describe(voice, Pcm.HashReference(reference))} " +
                  $"lang={language} chars={text.Length} text=\"{Preview(text)}\"");

        var sink = new SampleSink(writer, MaxSamples, record: _cache.Enabled);
        bool passthroughHandled = false;
        string error = "";

        if (_passthroughOnly)
        {
            passthroughHandled = TryPassthrough(text, language, reference, writer);
        }
        else
        {
            using var firstAudio = new CancellationTokenSource(FirstAudioBudget);
            bool heard = false;
            try
            {
                _provider.SpeakAsync(new SpeechRequest(text, language, reference, voice),
                        samples =>
                        {
                            if (!heard)
                            {
                                heard = true;
                                firstAudio.CancelAfter(Timeout.InfiniteTimeSpan);
                            }

                            sink.Add(samples.AsSpan());
                        }, firstAudio.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                error = !heard && firstAudio.IsCancellationRequested
                    ? $"no audio within {FirstAudioBudget.TotalSeconds:0} s"
                    : ex.Message;
                Log.Warn($"provider {_provider.Name} failed: {error}");
            }

            if (!heard && _passthrough != null && clock.Elapsed < PassthroughCutoff)
            {
                Log.Warn("trying the local model as a fallback");
                passthroughHandled = TryPassthrough(text, language, reference, writer);
            }
        }

        if (passthroughHandled)
        {
            Log.Info($"passthrough: {text.Length} chars in {clock.ElapsedMilliseconds} ms");
            return;
        }

        if (sink.Total == 0)
        {
            sink.PadSilence(SilencePadSamples);
            Log.Warn($"no audio produced for {text.Length} chars; sending {SilencePadSamples} silent samples" +
                     (error.Length > 0 ? $" ({error})" : ""));
        }

        sink.Flush();
        writer.Write(3);
        writer.Flush();

        // Read Total after Flush: the last partial chunk only counts once it is written.
        int produced = sink.Total;

        // A voice that changed during synthesis (a clone was made, or cloning fell back to a
        // default voice) would put this audio under the wrong key.
        if (_cache.Enabled && error.Length == 0 && produced > 0 && !sink.Capped &&
            sink.RecordedS16 is { Length: > 0 } audio && _provider.VoiceSignature(voice) == signature)
        {
            _cache.Put(key, audio);
        }

        Log.Info($"{CacheProviderName}: {text.Length} chars -> {produced} samples in {clock.ElapsedMilliseconds} ms" +
                 (error.Length > 0 ? " (silence fallback)" : ""));
    }

    private string CacheProviderName => _passthroughOnly ? "passthrough" : _provider.Name;

    private bool TryPassthrough(string text, string language, float[] reference, BinaryWriter writer)
        => _passthrough != null && _passthrough.TrySynthesize(text, language, reference, writer);

    private static string Preview(string text)
    {
        string flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= 80 ? flat : flat[..80] + "...";
    }

    private static bool SupportedLanguage(string language) => language switch
    {
        "English" or "Chinese" or "Japanese" or "Korean" or "German" or "French" or "Russian"
            or "Portuguese" or "Spanish" or "Italian" => true,
        _ => false,
    };

    public void Dispose()
    {
        _provider.Dispose();
        _passthrough?.Dispose();
    }
}
