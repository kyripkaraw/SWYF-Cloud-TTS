namespace SwyfCloudTts.Providers;

/// <summary>
/// OpenAI speech endpoint. <c>response_format: pcm</c> returns exactly what the game wants:
/// 24 kHz, 16-bit, mono, little endian. No resampling anywhere in the pipeline.
/// </summary>
internal sealed class OpenAiTts : HttpTtsProvider
{
    private readonly Config.OpenAiConfig _config;
    private readonly VoiceResolver _voices;

    public OpenAiTts(Config config, VoiceResolver voices) : base(config)
    {
        _config = config.OpenAi;
        _voices = voices;
    }

    public override string Name => "openai";

    public override bool Ready => !string.IsNullOrWhiteSpace(_config.ApiKey);

    public override string VoiceSignature(VoiceEntry voice)
        => _voices.PickOpenAiVoice(voice, _config.Voice) + "|" + _config.Model;

    public override Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token)
    {
        string voice = _voices.PickOpenAiVoice(request.Voice, _config.Voice);

        return RunAsync(onSamples, async (emit, ct) =>
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = _config.Model,
                ["voice"] = voice,
                ["input"] = request.Text,
                ["response_format"] = "pcm",
            };

            if (_config.Speed is > 0.25 and < 4.0)
            {
                payload["speed"] = _config.Speed.Value;
            }

            if (!string.IsNullOrWhiteSpace(_config.Instructions))
            {
                payload["instructions"] = _config.Instructions!;
            }

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post, _config.BaseUrl.TrimEnd('/') + "/audio/speech")
            {
                Content = Json(payload),
            };

            httpRequest.Headers.Authorization = Bearer(_config.ApiKey);

            using HttpResponseMessage response = await SendAsync(httpRequest, ct).ConfigureAwait(false);
            await StreamPcmAsync(response, emit, ct).ConfigureAwait(false);
        }, token);
    }
}
