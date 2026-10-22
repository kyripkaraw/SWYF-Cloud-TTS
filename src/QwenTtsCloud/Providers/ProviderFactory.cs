namespace SwyfCloudTts.Providers;

/// Single place that maps a config provider name to an implementation.
internal static class ProviderFactory
{
    public static ITtsProvider Create(Config config, VoiceResolver voices, out bool passthroughOnly)
    {
        string name = (config.Provider ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            name = "googleai";
        }

        passthroughOnly = false;
        switch (name)
        {
            case "googleai":
                return new GoogleAiTts(config, voices);
            case "elevenlabs":
                return new ElevenLabsTts(config, voices);
            case "openai":
                return new OpenAiTts(config, voices);
            case "qwenremote":
                return new QwenRemoteTts(config, voices);
            case "mock":
                return new MockTts();
            case "passthrough":
                passthroughOnly = true;
                return new SilentTts();
            case "silent":
                return new SilentTts();
            default:
                Log.Error($"unknown provider '{config.Provider}', falling back to googleai");
                return new GoogleAiTts(config, voices);
        }
    }
}
