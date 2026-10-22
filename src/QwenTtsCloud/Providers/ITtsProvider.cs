namespace SwyfCloudTts.Providers;

internal readonly record struct SpeechRequest(string Text, string Language, float[] Reference, VoiceEntry Voice);

internal interface ITtsProvider : IDisposable
{
    /// <summary>Config name, also used in cache keys and logs.</summary>
    string Name { get; }

    /// <summary>False when the provider is missing credentials; the app will not call it.</summary>
    bool Ready { get; }

    /// <summary>Stable cache-key component for the voice this speaker will use.</summary>
    string VoiceSignature(VoiceEntry voice);

    /// <summary>
    /// Synthesizes one game chunk (at most 2000 characters, usually ~240 UTF-8 bytes) and
    /// streams 24 kHz mono float samples through <paramref name="onSamples"/> as they arrive.
    /// </summary>
    Task SpeakAsync(SpeechRequest request, Action<float[]> onSamples, CancellationToken token);
}
