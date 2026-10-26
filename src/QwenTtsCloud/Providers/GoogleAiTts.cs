using System.Text;
using Newtonsoft.Json.Linq;

namespace SwyfCloudTts.Providers;

/// Google AI Studio (Gemini TTS), not Google Cloud Text-to-Speech.
/// The model speaks the text verbatim, so delivery hints go into speech_metadata,
/// never into the transcript. Output is requested as 24 kHz s16le, which is exactly
/// the format the game consumes - no resampling anywhere.
internal sealed class GoogleAiTts : HttpTtsProvider
{
    private const int SampleRate = 24000;

    /// Longer lines are split at sentence boundaries to keep each request small.
    private const int ChunkLimit = 600;

    private readonly Config.GoogleAiConfig _config;
    private readonly VoiceResolver _voices;

    public GoogleAiTts(Config config, VoiceResolver voices) : base(config)
    {
        _config = config.GoogleAi;
        _voices = voices;
    }

    public override string Name => "googleai";

    public override bool Ready => !string.IsNullOrWhiteSpace(_config.ApiKey);

    public override string VoiceSignature(VoiceEntry voice)
    {
        string voiceId = ResolveVoice(voice);
        string style = _config.Style ?? "";
        string styleHash = Compat.Sha256Hex(Encoding.UTF8.GetBytes(style), 4);
        return $"{voiceId}|{_config.Model}|{styleHash}";
    }

    public override Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token)
        => RunAsync(onSamples, async (emit, ct) =>
        {
            string voice = ResolveVoice(request.Voice);

            foreach (string chunk in SplitText(request.Text))
            {
                await SpeakChunkAsync(chunk, voice, emit, ct).ConfigureAwait(false);
            }
        }, token);

    private string ResolveVoice(VoiceEntry voice) => _voices.PickGoogleVoice(voice, _config.VoiceId);

    private async Task SpeakChunkAsync(string text, string voice, Action<float[]> onSamples, CancellationToken token)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _config.Model,
            ["input"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "user_input",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = text,
                            ["annotations"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "speech_metadata",
                                    ["style"] = _config.Style,
                                },
                            },
                        },
                    },
                },
            },
            ["response_format"] = new Dictionary<string, object?>
            {
                ["type"] = "audio",
                ["mime_type"] = "audio/l16",
                ["sample_rate"] = SampleRate,
            },
            ["generation_config"] = new Dictionary<string, object?>
            {
                ["speech_config"] = new object[]
                {
                    new Dictionary<string, object?> { ["voice"] = voice },
                },
            },
        };

        HttpCall httpRequest = HttpCall.PostJson(_config.BaseUrl.TrimEnd('/') + "/interactions", payload)
            .Header("x-goog-api-key", _config.ApiKey);

        using HttpReply response = await SendAsync(httpRequest, token).ConfigureAwait(false);
        JObject document = await response.ReadJsonAsync(token).ConfigureAwait(false);

        byte[] audio = ExtractAudio(document);
        if (audio.Length == 0)
        {
            throw new InvalidOperationException(
                "googleai returned no audio (safety block or unsupported voice?)");
        }

        audio = StripWavHeader(audio);
        EmitPcm(audio, onSamples);
    }

    /// Accepts both the Interactions and the generateContent response shapes.
    private static byte[] ExtractAudio(JObject root)
    {
        if (root["steps"] is JArray steps)
        {
            byte[] found = Array.Empty<byte>();
            foreach (JObject step in steps.OfType<JObject>())
            {
                if (step["content"] is not JArray content)
                {
                    continue;
                }

                foreach (JObject item in content.OfType<JObject>())
                {
                    if (String(item, "type") == "audio" && String(item, "data") is { } data)
                    {
                        found = Decode(data);
                    }
                }
            }

            if (found.Length > 0)
            {
                return found;
            }
        }

        if (root["candidates"] is JArray candidates)
        {
            foreach (JObject candidate in candidates.OfType<JObject>())
            {
                if (candidate["content"] is not JObject content || content["parts"] is not JArray parts)
                {
                    continue;
                }

                foreach (JObject part in parts.OfType<JObject>())
                {
                    if ((part["inlineData"] ?? part["inline_data"]) is JObject inline &&
                        String(inline, "data") is { } data)
                    {
                        return Decode(data);
                    }
                }
            }
        }

        if (root["output_audio"] is JObject output && String(output, "data") is { } direct)
        {
            return Decode(direct);
        }

        return Array.Empty<byte>();
    }

    private static string? String(JObject element, string name)
        => element[name] is JValue { Type: JTokenType.String } value ? value.Value<string>() : null;

    private static byte[] Decode(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return Array.Empty<byte>();
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    /// Slices the data chunk out of a RIFF/WAVE payload; returns input unchanged for raw PCM.
    internal static byte[] StripWavHeader(byte[] audio)
    {
        if (audio.Length < 12 ||
            audio[0] != 'R' || audio[1] != 'I' || audio[2] != 'F' || audio[3] != 'F')
        {
            return audio;
        }

        int offset = 12;
        while (offset + 8 <= audio.Length)
        {
            string id = Encoding.ASCII.GetString(audio, offset, 4);
            int size = BitConverter.ToInt32(audio, offset + 4);
            if (size < 0)
            {
                break;
            }

            int start = offset + 8;
            if (id == "data")
            {
                int length = Math.Min(size, audio.Length - start);
                var data = new byte[Math.Max(0, length)];
                Array.Copy(audio, start, data, 0, data.Length);
                return data;
            }

            offset = start + size + (size & 1);
        }

        if (audio.Length <= 44)
        {
            return Array.Empty<byte>();
        }

        var body = new byte[audio.Length - 44];
        Array.Copy(audio, 44, body, 0, body.Length);
        return body;
    }

    private static void EmitPcm(byte[] pcm, Action<float[]> onSamples)
    {
        var converter = new Pcm.S16ToFloat();
        float[] samples = converter.Push(pcm, 0, pcm.Length);
        if (samples.Length > 0)
        {
            onSamples(samples);
        }

        float[] tail = converter.Flush();
        if (tail.Length > 0)
        {
            onSamples(tail);
        }
    }

    internal static IEnumerable<string> SplitText(string text)
    {
        if (text.Length <= ChunkLimit)
        {
            yield return text;
            yield break;
        }

        var sentences = new List<string>();
        var current = new StringBuilder();

        foreach (char c in text)
        {
            current.Append(c);
            bool terminator = c is '.' or '!' or '?' or '…' or '\n';
            if (terminator && current.Length >= 120)
            {
                sentences.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            sentences.Add(current.ToString().Trim());
        }

        var merged = new StringBuilder();
        foreach (string sentence in sentences)
        {
            if (sentence.Length == 0)
            {
                continue;
            }

            if (merged.Length > 0 && merged.Length + 1 + sentence.Length > ChunkLimit)
            {
                yield return merged.ToString();
                merged.Clear();
            }

            if (sentence.Length > ChunkLimit)
            {
                if (merged.Length > 0)
                {
                    yield return merged.ToString();
                    merged.Clear();
                }

                for (int i = 0; i < sentence.Length; i += ChunkLimit)
                {
                    yield return sentence.Substring(i, Math.Min(ChunkLimit, sentence.Length - i));
                }

                continue;
            }

            if (merged.Length > 0)
            {
                merged.Append(' ');
            }

            merged.Append(sentence);
        }

        if (merged.Length > 0)
        {
            yield return merged.ToString();
        }
    }
}
