using System.Net;
using System.Net.Http.Headers;

namespace SwyfCloudTts.Providers;

/// Qwen3-TTS on a server the user runs (rented GPU, home box, RunPod/Vast pod). Two dialects
/// are supported:
///
/// 1. The OpenAI-compatible one most Qwen3-TTS wrappers expose: <c>POST /v1/audio/speech</c>
///    (JSON, named voice) and <c>POST /v1/audio/speech/clone</c> (multipart, reference clip).
/// 2. A small custom wrapper that exposes only <c>POST /tts</c> with
///    <c>{ text, voice_prompt, language }</c> and an <c>X-API-Key</c> header (VoiceDesign).
///
/// "clone" mode sends the reference clip the game already ships with every line, so each
/// character keeps their own in-game voice. "voice" mode uses a clip preloaded on the server
/// and is faster because nothing has to be encoded per request. "design" mode asks the
/// server to invent the voice from the instruct text on every line.
///
/// If the OpenAI endpoint is missing (404/405), the provider retries the same line against
/// <c>/tts</c> once and remembers the dialect for the rest of the session.
internal sealed class QwenRemoteTts : HttpTtsProvider
{
    /// Half a second at 24 kHz; anything shorter is not worth uploading as a reference.
    private const int MinCloneSamples = 12000;

    private const int DialectTts = 2;

    private readonly Config.QwenRemoteConfig _config;
    private readonly VoiceResolver _voices;

    /// Set after the first rejected clone call; avoids paying for a doomed upload per line.
    private volatile bool _cloneUnavailable;

    /// Set once the server is known to speak the <c>/tts</c> VoiceDesign dialect.
    private volatile int _dialect;

    private const string DefaultPrompt = "A natural, expressive voice with clear diction.";

    public QwenRemoteTts(Config config, VoiceResolver voices) : base(config)
    {
        _config = config.QwenRemote;
        _voices = voices;
    }

    public override string Name => "qwenremote";

    public override bool Ready => TryRoot(out _);

    private string Mode => (_config.Mode ?? "").Trim().ToLowerInvariant();

    private bool VoiceMode => Mode == "voice";

    private bool DesignMode => Mode == "design";

    public override string VoiceSignature(VoiceEntry voice)
    {
        // A /tts VoiceDesign server has no reference clip or named voices: the prompt is the voice.
        if (DesignMode || _dialect == DialectTts)
        {
            return "design|" + ResolvePrompt(voice) + "|" + (_config.Model ?? "");
        }

        // Clone mode keys the cache by speaker, because the reference clip *is* the voice.
        if (!VoiceMode && !_cloneUnavailable)
        {
            return "clone|" + voice.Id;
        }

        return "voice|" + ResolveVoice(voice) + "|" + (_config.Model ?? "");
    }

    public override Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token)
    {
        return RunAsync(onSamples, async (emit, ct) =>
        {
            byte[] audio = await RequestAsync(request, ct).ConfigureAwait(false);

            float[] samples = Decode(audio);
            if (samples.Length == 0)
            {
                throw new InvalidOperationException("qwenremote returned no audio samples");
            }

            const int chunk = 4800; // 200 ms, so the game starts playing immediately
            for (int offset = 0; offset < samples.Length; offset += chunk)
            {
                int length = Math.Min(chunk, samples.Length - offset);
                emit(samples.AsSpan(offset, length).ToArray());
            }
        }, token);
    }

    private bool ShouldClone(SpeechRequest request)
        => !DesignMode && !VoiceMode && !_cloneUnavailable && request.Reference.Length >= MinCloneSamples;

    /// True for servers that have no clone endpoint, or that reject the reference clip.
    private static bool IsCloneRejected(HttpRequestException ex) => ex.StatusCode is
        HttpStatusCode.BadRequest or
        HttpStatusCode.NotFound or
        HttpStatusCode.MethodNotAllowed or
        HttpStatusCode.UnsupportedMediaType or
        HttpStatusCode.UnprocessableEntity;

    private async Task<byte[]> RequestAsync(SpeechRequest request, CancellationToken token)
    {
        if (DesignMode || _dialect == DialectTts)
        {
            return await RequestDesignAsync(request, token).ConfigureAwait(false);
        }

        try
        {
            if (ShouldClone(request))
            {
                try
                {
                    return await RequestCloneAsync(request, token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (IsCloneRejected(ex) && ResolveVoice(request.Voice).Length > 0)
                {
                    _cloneUnavailable = true;
                    Log.Warn($"{Name}: cloning is not available on this server ({ex.Message}); " +
                             $"falling back to the server voice '{ResolveVoice(request.Voice)}'");
                }
            }

            return await RequestVoiceAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            // Some self-hosted wrappers expose only POST /tts. Try it once and remember it,
            // so later lines skip the failing request.
            Log.Warn($"{Name}: {Endpoint("/audio/speech")} is not available ({ex.Message}); trying /tts");
            try
            {
                byte[] audio = await RequestDesignAsync(request, token).ConfigureAwait(false);
                _dialect = DialectTts;
                Log.Info($"{Name}: using the server's /tts VoiceDesign endpoint");
                return audio;
            }
            catch (HttpRequestException ttsEx)
            {
                TryRoot(out string root);
                throw new HttpRequestException(
                    $"{Name}: neither /v1/audio/speech nor /tts exists on {root} ({ttsEx.Message}); " +
                    "check the server URL and port", ttsEx);
            }
        }
    }

    private async Task<byte[]> RequestDesignAsync(SpeechRequest request, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_prompt"] = ResolvePrompt(request.Voice),
        };

        string language = ResolveLanguage(request);
        if (language.Length > 0)
        {
            payload["language"] = language;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, RootEndpoint("/tts"))
        {
            Content = Json(payload),
        };

        // The reference wrapper checks X-API-Key; a proxy in front of it may want Bearer as well.
        string key = (_config.ApiKey ?? "").Trim();
        if (key.Length > 0)
        {
            httpRequest.Headers.TryAddWithoutValidation("X-API-Key", key);
            httpRequest.Headers.Authorization = Bearer(key);
        }

        using HttpResponseMessage response = await SendQwenAsync(httpRequest, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    private async Task<byte[]> RequestCloneAsync(SpeechRequest request, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(request.Text), "text");

        var reference = new ByteArrayContent(Pcm.ToWav(request.Reference));
        reference.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(reference, "reference_audio", "reference.wav");

        form.Add(new StringContent("wav"), "response_format");

        string language = ResolveLanguage(request);
        if (language.Length > 0)
        {
            form.Add(new StringContent(language), "language");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint("/audio/speech/clone"))
        {
            Content = form,
        };
        AddAuth(httpRequest);

        using HttpResponseMessage response = await SendQwenAsync(httpRequest, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    private async Task<byte[]> RequestVoiceAsync(SpeechRequest request, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(_config.Model) ? "tts-1" : _config.Model,
            ["input"] = request.Text,
            ["response_format"] = "wav",
        };

        string voice = ResolveVoice(request.Voice);
        if (voice.Length > 0)
        {
            payload["voice"] = voice;
        }

        string language = ResolveLanguage(request);
        if (language.Length > 0)
        {
            payload["language"] = language;
        }

        if (!string.IsNullOrWhiteSpace(_config.Instruct))
        {
            payload["instruct"] = _config.Instruct;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint("/audio/speech"))
        {
            Content = Json(payload),
        };
        AddAuth(httpRequest);

        using HttpResponseMessage response = await SendQwenAsync(httpRequest, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }

    /// A wrong URL or a stopped pod is the most common failure for a self-hosted server, and the
    /// stock SocketException text does not say what to check. Status failures keep their own text.
    private async Task<HttpResponseMessage> SendQwenAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            return await SendAsync(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            TryRoot(out string root);
            throw new HttpRequestException(
                $"{Name}: cannot reach {root} ({ex.Message}) | check that the pod is running, the URL is " +
                "correct and the API port (usually 8000) is exposed", ex);
        }
    }

    private void AddAuth(HttpRequestMessage request)
    {
        string key = (_config.ApiKey ?? "").Trim();
        if (key.Length > 0)
        {
            request.Headers.Authorization = Bearer(key);
        }
    }

    /// Accepts both "http://host:8000" and "http://host:8000/v1", because people copy either
    /// form from the docs. Returns the server root without a trailing slash.
    private bool TryRoot(out string root)
    {
        root = (_config.BaseUrl ?? "").Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^3].TrimEnd('/');
        }

        return Uri.TryCreate(root, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private string Endpoint(string suffix) => RootEndpoint("/v1" + suffix);

    private string RootEndpoint(string suffix)
    {
        TryRoot(out string root);
        return root + suffix;
    }

    /// Per-character mapping wins, then the global server voice.
    private string ResolveVoice(VoiceEntry voice)
        => _voices.PickQwenVoice(voice, _config.Voice);

    /// The /tts wrapper takes a free-form voice description from "instruct"; when it is empty,
    /// a short gender hint keeps the characters distinct.
    private string ResolvePrompt(VoiceEntry voice)
    {
        string prompt = (_config.Instruct ?? "").Trim();
        if (prompt.Length > 0)
        {
            return prompt;
        }

        if (string.Equals(voice.Gender, "female", StringComparison.OrdinalIgnoreCase))
        {
            return "A natural, expressive female voice with clear diction.";
        }

        if (string.Equals(voice.Gender, "male", StringComparison.OrdinalIgnoreCase))
        {
            return "A natural, expressive male voice with clear diction.";
        }

        return DefaultPrompt;
    }

    /// "auto" follows the language the game asks for; the game's list matches Qwen3-TTS.
    private string ResolveLanguage(SpeechRequest request)
    {
        string configured = (_config.Language ?? "").Trim();
        if (configured.Length == 0 || configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return request.Language;
        }

        return configured;
    }

    /// Qwen servers normally answer with a 24 kHz WAV. A few builds ignore
    /// <c>response_format</c> and stream raw 16-bit mono PCM, so both are accepted.
    private static float[] Decode(byte[] audio)
    {
        if (audio.Length == 0)
        {
            throw new InvalidOperationException("qwenremote returned an empty audio file");
        }

        if (audio.Length >= 12 &&
            audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F' &&
            audio[8] == 'W' && audio[9] == 'A' && audio[10] == 'V' && audio[11] == 'E')
        {
            return Pcm.FromWav(audio);
        }

        if ((audio.Length & 1) != 0)
        {
            throw new InvalidDataException("qwenremote returned neither a WAV file nor 16-bit PCM");
        }

        var converter = new Pcm.S16ToFloat();
        float[] head = converter.Push(audio);
        float[] tail = converter.Flush();
        if (tail.Length == 0)
        {
            return head;
        }

        var combined = new float[head.Length + tail.Length];
        Array.Copy(head, combined, head.Length);
        Array.Copy(tail, 0, combined, head.Length, tail.Length);
        return combined;
    }
}
