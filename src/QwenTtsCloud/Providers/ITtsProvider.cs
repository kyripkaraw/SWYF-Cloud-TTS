namespace SwyfCloudTts.Providers;

internal readonly record struct SpeechRequest(string Text, string Language, float[] Reference, VoiceEntry Voice);

internal interface ITtsProvider : IDisposable
{
    /// Config name, also used in cache keys and logs.
    string Name { get; }

    /// False when the provider is missing credentials; the app will not call it.
    bool Ready { get; }

    /// Stable cache-key component for the voice this speaker will use.
    string VoiceSignature(VoiceEntry voice);

    /// Synthesizes one game chunk (at most 2000 characters, usually ~240 UTF-8 bytes) and
    /// streams 24 kHz mono float samples through <paramref name="onSamples"/> as they arrive.
    Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token);
}
