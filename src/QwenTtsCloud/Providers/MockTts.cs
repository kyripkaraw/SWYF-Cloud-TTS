namespace SwyfCloudTts.Providers;

/// Offline provider used by the protocol probe. It turns text into a deterministic
/// "babble" so the pipe, chunking and timing can be tested without an API key.
internal sealed class MockTts : ITtsProvider
{
    public string Name => "mock";

    public bool Ready => true;

    public string VoiceSignature(VoiceEntry voice) => "mock";

    public async Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token)
    {
        double baseHz = request.Voice.PitchHz > 0
            ? Math.Clamp(request.Voice.PitchHz, 80, 300)
            : (request.Voice.Gender == "male" ? 120 : 205);

        int seed = 17;
        foreach (char c in request.Text)
        {
            seed = unchecked(seed * 31 + c);
        }

        // Roughly one syllable per 4 characters, 130 ms each.
        int syllables = Math.Max(1, request.Text.Length / 4);
        const int syllableSamples = 3120;

        for (int s = 0; s < syllables; s++)
        {
            token.ThrowIfCancellationRequested();

            var buffer = new float[syllableSamples];
            double hz = baseHz * (0.85 + 0.3 * ((seed >> (s % 16)) & 3) / 3.0);

            for (int i = 0; i < buffer.Length; i++)
            {
                double t = (double)i / Pcm.SampleRate;
                double envelope = Math.Sin(Math.PI * i / buffer.Length);
                double sample = Math.Sin(2 * Math.PI * hz * t)
                                + 0.35 * Math.Sin(4 * Math.PI * hz * t)
                                + 0.15 * Math.Sin(6 * Math.PI * hz * t);
                buffer[i] = (float)(0.18 * envelope * sample);
            }

            onSamples(buffer);
            await Task.Delay(15, token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
    }
}

/// Produces no audio at all; used when nothing is configured. The app pads silence.
internal sealed class SilentTts : ITtsProvider
{
    public string Name => "silent";

    public bool Ready => true;

    public string VoiceSignature(VoiceEntry voice) => "silent";

    public Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token)
        => Task.CompletedTask;

    public void Dispose()
    {
    }
}
