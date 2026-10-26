using System.Net;

namespace SwyfCloudTts.Providers;

/// ElevenLabs. With autoClone every character speaks with a clone of the reference clip the game
/// sends, which keeps their identity; <see cref="ElevenLabsClones"/> fits those clones into the
/// account's voice slots. Characters without a clone get a default voice matching their pitch.
internal sealed class ElevenLabsTts : HttpTtsProvider
{
    private readonly Config.ElevenLabsConfig _config;
    private readonly VoiceResolver _voices;
    private readonly ElevenLabsClones _clones;

    public ElevenLabsTts(Config config, VoiceResolver voices) : base(config)
    {
        _config = config.ElevenLabs;
        _voices = voices;
        _clones = new ElevenLabsClones(config, voices, SendAsync);
    }

    public override string Name => "elevenlabs";

    public override bool Ready => !string.IsNullOrWhiteSpace(_config.ApiKey);

    /// Clones are keyed by character rather than by voice id, so a character whose clone was rotated
    /// out still gets cached lines back without being cloned again.
    public override string VoiceSignature(VoiceEntry voice)
    {
        string mapped = _voices.GetVoiceId(voice, Name);
        string id = mapped.Length > 0
            ? (_clones.IsOwnClone(mapped) ? "clone:" + voice.Id : mapped)
            : (WillClone(voice) ? "clone:" + voice.Id : FallbackVoice(voice));
        return $"{id}|{_config.ModelId}";
    }

    public override Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token)
        => RunAsync(onSamples, async (emit, ct) =>
        {
            string voiceId = await ResolveVoiceAsync(request, ct).ConfigureAwait(false);
            try
            {
                await StreamAsync(voiceId, request.Text, emit, ct).ConfigureAwait(false);
            }
            catch (HttpFailure ex) when (IsMissingVoice(ex) && _clones.IsOwnClone(voiceId))
            {
                Log.Warn($"clone {voiceId} of speaker {request.Voice.Id} was deleted; getting a new voice");
                _clones.Forget(voiceId, request.Voice);
                voiceId = await ResolveVoiceAsync(request, ct).ConfigureAwait(false);
                await StreamAsync(voiceId, request.Text, emit, ct).ConfigureAwait(false);
            }

            _clones.Touch(voiceId);
        }, token);

    public Task<int> SaveAccountVoicesAsync(CancellationToken token) => _clones.SaveAccountVoicesAsync(token);

    private bool WillClone(VoiceEntry voice) => _config.AutoClone && _clones.CanTry(voice.Id);

    private async Task<string> ResolveVoiceAsync(SpeechRequest request, CancellationToken token)
    {
        string mapped = _voices.GetVoiceId(request.Voice, Name);
        if (mapped.Length > 0)
        {
            return mapped;
        }

        // Test lines have no game clip; never clone from those.
        if (WillClone(request.Voice) && request.Reference.Length >= ElevenLabsClones.MinReferenceSamples)
        {
            string? cloned = await _clones.AcquireAsync(request.Voice, request.Reference, token).ConfigureAwait(false);
            if (cloned != null)
            {
                return cloned;
            }
        }

        return FallbackVoice(request.Voice);
    }

    private string FallbackVoice(VoiceEntry voice) => _voices.PickElevenLabsVoice(voice, _config.DefaultVoiceId);

    /// The streaming endpoint delivers the first audio long before the whole line is ready.
    private async Task StreamAsync(string voiceId, string text, Action<float[]> emit, CancellationToken token)
    {
        var payload = new Dictionary<string, object>
        {
            ["text"] = text,
            ["model_id"] = _config.ModelId,
            ["voice_settings"] = new Dictionary<string, object>
            {
                ["stability"] = _config.Stability,
                ["similarity_boost"] = _config.SimilarityBoost,
            },
        };

        string url = $"{_config.BaseUrl.TrimEnd('/')}/text-to-speech/{Uri.EscapeDataString(voiceId)}/stream" +
                     "?output_format=pcm_24000";

        HttpCall httpRequest = HttpCall.PostJson(url, payload).Header("xi-api-key", _config.ApiKey);

        using HttpReply response = await SendAsync(httpRequest, token).ConfigureAwait(false);
        await StreamPcmAsync(response, emit, token).ConfigureAwait(false);
    }

    private static bool IsMissingVoice(HttpFailure ex)
        => ex.StatusCode == HttpStatusCode.NotFound ||
           (ex.StatusCode == HttpStatusCode.BadRequest &&
            ex.Message.IndexOf("voice_not_found", StringComparison.OrdinalIgnoreCase) >= 0);
}
