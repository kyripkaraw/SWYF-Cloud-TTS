using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SwyfCloudTts.Providers;

/// <summary>Shared plumbing: JSON posts, streaming PCM reads, retry and timeout policy.</summary>
internal abstract class HttpTtsProvider : ITtsProvider
{
    private static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestVersion = HttpVersion.Version11,
    };

    private readonly int _timeoutSeconds;
    private readonly int _attempts;

    protected HttpTtsProvider(Config config)
    {
        _timeoutSeconds = Math.Clamp(config.Network.TimeoutSeconds, 2, 60);
        _attempts = Math.Clamp(config.Network.MaxAttempts, 1, 5);
    }

    public abstract string Name { get; }

    public abstract bool Ready { get; }

    public abstract string VoiceSignature(VoiceEntry voice);

    public virtual void Dispose()
    {
    }

    public abstract Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token);

    /// <summary>
    /// Executes a request with retries. The timeout counts from the last audio received, so a long
    /// line can keep streaming. Only attempts that produced no audio are retried, so the game never
    /// hears a phrase twice.
    /// </summary>
    protected async Task RunAsync(Action<float[]> onSamples, Func<Action<float[]>, CancellationToken, Task> attempt,
                                  CancellationToken token)
    {
        TimeSpan idleLimit = TimeSpan.FromSeconds(_timeoutSeconds);
        Exception? last = null;
        for (int i = 1; i <= _attempts; i++)
        {
            bool produced = false;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(idleLimit);

            void Forward(float[] samples)
            {
                produced = true;
                idle.CancelAfter(idleLimit);
                onSamples(samples);
            }

            try
            {
                await attempt(Forward, idle.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                last = new TimeoutException($"{Name} sent nothing for {_timeoutSeconds}s");
                Log.Warn($"attempt {i}/{_attempts}: {last.Message}");
                if (produced)
                {
                    throw last;
                }
            }
            catch (Exception ex) when (!produced && i < _attempts && IsTransient(ex))
            {
                last = ex;
                Log.Warn($"attempt {i}/{_attempts} failed: {ex.Message}");
                await Task.Delay(250 * i, token).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException($"{Name} request failed");
    }

    protected static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException http => http.StatusCode is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout,
        IOException => true,
        _ => false,
    };

    /// <summary>Streams a raw 16-bit mono PCM response body, 24 kHz by contract of both APIs.</summary>
    protected static async Task StreamPcmAsync(HttpResponseMessage response, Action<float[]> onSamples, CancellationToken token)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var converter = new Pcm.S16ToFloat();
        byte[] buffer = new byte[16 * 1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            float[] samples = converter.Push(buffer.AsSpan(0, read));
            if (samples.Length > 0)
            {
                onSamples(samples);
            }
        }

        float[] tail = converter.Flush();
        if (tail.Length > 0)
        {
            onSamples(tail);
        }
    }

    /// <summary>Forces a useful error message instead of "response status code does not indicate success".</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        HttpResponseMessage response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch
        {
            body = "";
        }

        body = body.Length > 400 ? body[..400] : body;
        body = Sanitize(body);
        HttpStatusCode code = response.StatusCode;
        response.Dispose();

        throw new HttpRequestException(
            $"{Name} HTTP {(int)code} {code}: {body}{Hint(code)}", null, code);
    }

    /// <summary>Plain-language hint for the failures users actually hit.</summary>
    private string Hint(HttpStatusCode code) => (Name, code) switch
    {
        ("elevenlabs", HttpStatusCode.Unauthorized) =>
            " | check the API key scopes (text_to_speech, voices_read; voices_write for cloning)",
        ("elevenlabs", HttpStatusCode.PaymentRequired) =>
            " | the current ElevenLabs plan does not allow this; free plans cannot use library voices or clone",
        ("elevenlabs", HttpStatusCode.TooManyRequests) =>
            " | ElevenLabs quota exhausted for this period",
        ("googleai", HttpStatusCode.BadRequest) =>
            " | check the model name, voice id and request size",
        ("googleai", HttpStatusCode.Forbidden) =>
            " | check that the key is valid and the Generative Language API is enabled",
        ("googleai", HttpStatusCode.TooManyRequests) =>
            " | Gemini free-tier rate limit; wait a moment or switch model to gemini-3.8-flash-lite-tts",
        ("openai", HttpStatusCode.Unauthorized) =>
            " | invalid OpenAI API key",
        ("openai", HttpStatusCode.PaymentRequired) or ("openai", HttpStatusCode.TooManyRequests) =>
            " | OpenAI quota/billing problem",
        ("qwenremote", HttpStatusCode.Unauthorized) or ("qwenremote", HttpStatusCode.Forbidden) =>
            " | check the server token (qwenremote.apiKey) or leave it empty when the server has no auth",
        ("qwenremote", HttpStatusCode.NotFound) =>
            " | check the server URL and that the Qwen3-TTS API (not just the Gradio UI) is on this port",
        ("qwenremote", HttpStatusCode.BadRequest) or ("qwenremote", HttpStatusCode.UnprocessableEntity) =>
            " | check mode/model/voice and whether this server supports /v1/audio/speech/clone",
        ("qwenremote", HttpStatusCode.ServiceUnavailable) =>
            " | the GPU pod is probably still loading the model; try again in a minute",
        _ => "",
    };

    protected static StringContent Json<T>(T value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    /// <summary>Keeps error text on one short line so a provider error cannot spam the log.</summary>
    private static string Sanitize(string body)
    {
        var builder = new StringBuilder(body.Length);
        bool inTag = false;
        foreach (char c in body)
        {
            if (c == '<')
            {
                inTag = true;
                continue;
            }

            if (c == '>')
            {
                inTag = false;
                continue;
            }

            if (inTag)
            {
                continue;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        string text = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    protected static AuthenticationHeaderValue Bearer(string key) => new("Bearer", key);
}
