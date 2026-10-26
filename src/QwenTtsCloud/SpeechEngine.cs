using System.Diagnostics;
using SwyfCloudTts.Providers;

namespace SwyfCloudTts;

/// Turns one line of the game into audio: picks the character's voice, answers from the disk
/// cache when it can, otherwise streams the line from the cloud provider. Shared by the worker
/// process and the in-game plugin; the caller decides where the audio goes.
internal sealed class SpeechEngine : IDisposable
{
    public const int MaxSamples = 720000;

    /// 50 ms of silence, enough to satisfy "produced some speech" checks.
    public const int SilencePadSamples = 1200;

    /// The game gives up on Qwen for the whole session when the first audio of a line takes
    /// more than 20 s, so a line still silent after this budget is answered with silence instead.
    private static readonly TimeSpan FirstAudioBudget = TimeSpan.FromSeconds(15);

    /// The local model needs most of the game's 20 s just to load.
    private static readonly TimeSpan FallbackCutoff = TimeSpan.FromSeconds(5);

    private readonly VoiceResolver _voices;
    private readonly AudioCache _cache;
    private readonly ITtsProvider _provider;

    private SpeechEngine(Config config, ITtsProvider provider, bool passthroughOnly, VoiceResolver voices, AudioCache cache)
    {
        Config = config;
        _provider = provider;
        PassthroughOnly = passthroughOnly;
        _voices = voices;
        _cache = cache;
    }

    public Config Config { get; }

    public string ProviderName => _provider.Name;

    /// The provider is "passthrough": the game's own model speaks, not the cloud.
    public bool PassthroughOnly { get; }

    public bool Ready => _provider.Ready;

    public bool CacheEnabled => _cache.Enabled;

    public static SpeechEngine Create(Config config)
    {
        var voices = new VoiceResolver(config);
        var cache = new AudioCache(config);
        ITtsProvider provider = ProviderFactory.Create(config, voices, out bool passthroughOnly);

        if (!passthroughOnly && !provider.Ready)
        {
            Log.Error(provider.Name == "qwenremote"
                ? $"provider 'qwenremote' has no valid server URL; set qwenremote.baseUrl in " +
                  $"{config.Path} or through SWYF_TTS_QWENREMOTE_URL (an optional token comes from " +
                  "SWYF_TTS_QWENREMOTE_KEY / QWEN_TTS_API_KEY)"
                : $"provider '{provider.Name}' has no credentials; set an api key in " +
                  $"{config.Path} or through SWYF_TTS_GOOGLEAI_KEY / SWYF_TTS_ELEVENLABS_KEY / " +
                  "SWYF_TTS_OPENAI_KEY");
        }

        return new SpeechEngine(config, provider, passthroughOnly, voices, cache);
    }

    /// Speaks one line into <paramref name="output"/>, in chunks of at most 4800 samples, and
    /// always produces some audio: silence stands in for a failed request. When the provider
    /// stays silent for the first few seconds, <paramref name="fallback"/> may answer the line
    /// itself and return true, which Speak then returns. Cancelling <paramref name="token"/>
    /// abandons the line.
    public bool Speak(string text, string language, float[] reference, Action<float[]> output,
                      Func<bool>? fallback = null, CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew();
        VoiceEntry voice = _voices.Resolve(reference, language);

        string signature = _provider.VoiceSignature(voice);
        string key = AudioCache.Key(_provider.Name, signature, language, text);

        if (_cache.TryGet(key, out byte[] cached))
        {
            int samples = SampleSink.PumpS16(cached, MaxSamples, output);
            Log.Info($"cache hit: {text.Length} chars -> {samples} samples in {clock.ElapsedMilliseconds} ms");
            return false;
        }

        Log.Debug($"synth: voice={VoiceResolver.Describe(voice, Pcm.HashReference(reference))} " +
                  $"lang={language} chars={text.Length} text=\"{Preview(text)}\"");

        var sink = new SampleSink(output, MaxSamples, record: _cache.Enabled);
        string error = "";
        long firstAudioMs = -1;

        using (var firstAudio = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            firstAudio.CancelAfter(FirstAudioBudget);
            try
            {
                _provider.SpeakAsync(new SpeechRequest(text, language, reference, voice),
                        samples =>
                        {
                            if (firstAudioMs < 0)
                            {
                                firstAudioMs = clock.ElapsedMilliseconds;
                                firstAudio.CancelAfter(Timeout.InfiniteTimeSpan);
                            }

                            sink.Add(samples);
                        }, firstAudio.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                Log.Info($"{_provider.Name}: line cancelled by the game after {clock.ElapsedMilliseconds} ms");
                throw new OperationCanceledException(token);
            }
            catch (Exception ex)
            {
                error = firstAudioMs < 0 && firstAudio.IsCancellationRequested
                    ? $"no audio within {FirstAudioBudget.TotalSeconds:0} s"
                    : ex.Message;
                Log.Warn($"provider {_provider.Name} failed: {error}");
            }
        }

        if (firstAudioMs < 0 && fallback != null && clock.Elapsed < FallbackCutoff)
        {
            Log.Warn("trying the local model as a fallback");
            if (fallback())
            {
                Log.Info($"passthrough: {text.Length} chars in {clock.ElapsedMilliseconds} ms");
                return true;
            }
        }

        if (sink.Total == 0)
        {
            sink.PadSilence(SilencePadSamples);
            Log.Warn($"no audio produced for {text.Length} chars; sending {SilencePadSamples} silent samples" +
                     (error.Length > 0 ? $" ({error})" : ""));
        }

        sink.Flush();

        // Read Total after Flush: the last partial chunk only counts once it is written.
        int produced = sink.Total;

        // A voice that changed during synthesis (a clone was made, or cloning fell back to a
        // default voice) would put this audio under the wrong key.
        if (_cache.Enabled && error.Length == 0 && produced > 0 && !sink.Capped &&
            sink.RecordedS16 is { Length: > 0 } audio && _provider.VoiceSignature(voice) == signature)
        {
            _cache.Put(key, audio);
        }

        Log.Info($"{_provider.Name}: {text.Length} chars -> {produced} samples in {clock.ElapsedMilliseconds} ms" +
                 (firstAudioMs >= 0 ? $", first audio after {firstAudioMs} ms" : "") +
                 (error.Length > 0 ? " (silence fallback)" : ""));
        return false;
    }

    private static string Preview(string text)
    {
        string flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= 80 ? flat : flat.Substring(0, 80) + "...";
    }

    public void Dispose() => _provider.Dispose();
}
