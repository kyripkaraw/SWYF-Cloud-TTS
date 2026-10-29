using SwyfCloudTts.Providers;

namespace SwyfCloudTts;

/// Protocol implementation. Mirrors the original worker's validation exactly, then hands the
/// line to <see cref="SpeechEngine"/> and streams the audio back in the same format.
///
/// Two protocol revisions exist. Game 0.1.83 sends Load(talker, codec, backend) and expects
/// "1, 1" back. Game 0.1.95 (the audio.cpp runtime, manifest "protocol": 2) sends Load(model,
/// backend), expects "1, 2, backend name" back, and sends a zero-length reference when the
/// speaker is the same as in the previous line.
internal sealed class App : IDisposable
{
    private readonly SpeechEngine _engine;
    private readonly PassthroughClient? _passthrough;
    private readonly bool _passthroughOnly;

    private bool _loaded;
    private int _protocol = 1;

    /// The last reference the game sent; protocol 2 refers to it with a zero length.
    private float[]? _reference;

    private App(SpeechEngine engine, bool passthroughOnly, PassthroughClient? passthrough)
    {
        _engine = engine;
        _passthroughOnly = passthroughOnly;
        _passthrough = passthrough;
    }

    public static App Create()
    {
        Config config = Config.Load();
        Log.Init(config.StateDirectory, config.Logging.Verbose);
        Log.Info($"QwenTtsCloud worker; state dir {config.StateDirectory}");
        Log.Info($"config: {config.Path}");

        PassthroughClient? passthrough = null;

        if (config.Fallback.Passthrough)
        {
            passthrough = new PassthroughClient(config);
            if (!passthrough.Available)
            {
                Log.Warn("fallback.passthrough is enabled but " + config.Fallback.OriginalExe +
                         " is missing; the installer should have kept a copy of the original worker");
                passthrough = null;
            }
        }

        SpeechEngine engine = SpeechEngine.Create(config);
        bool passthroughOnly = engine.PassthroughOnly;

        if (engine.ProviderName == "silent" && passthroughOnly && passthrough == null)
        {
            Log.Error("provider 'passthrough' needs fallback.passthrough and QwenTtsWorker.orig.exe; emitting silence");
            passthroughOnly = false;
        }

        if (!passthroughOnly && !engine.Ready)
        {
            if (passthrough != null)
            {
                Log.Warn("switching to the local model through passthrough");
                passthroughOnly = true;
            }
            else
            {
                Log.Error("no fallback available; the game will hear silence");
            }
        }

        Log.Info($"provider={engine.ProviderName}{(passthroughOnly && !engine.PassthroughOnly ? " (passthrough)" : "")}, " +
                 $"cache={engine.CacheEnabled}, passthrough={passthrough != null}");

        return new App(engine, passthroughOnly, passthrough);
    }

    public void Load(BinaryReader reader, BinaryWriter writer)
    {
        string model = reader.ReadString();
        string second = reader.ReadString();

        // Protocol 2 sends the backend second; protocol 1 sends the codec path there.
        bool current = IsBackend(second);
        string backend = current ? second : reader.ReadString();

        if (_loaded || !IsBackend(backend))
        {
            throw new InvalidDataException("Invalid Qwen load request.");
        }

        _loaded = true;
        _protocol = current ? 2 : 1;

        if (current)
        {
            _passthrough?.RememberLoad(2, model, backend);
            Log.Info($"load: model={Path.GetFileName(model)}, backend={backend}, protocol 2");

            // The game accepts cuda, vulkan or cpu; the cloud needs neither GPU.
            writer.Write(1);
            writer.Write(2);
            writer.Write("cpu");
        }
        else
        {
            _passthrough?.RememberLoad(1, model, second, backend);
            Log.Info($"load: talker={Path.GetFileName(model)}, codec={Path.GetFileName(second)}, backend={backend}");

            writer.Write(1);
            writer.Write(1);
        }

        writer.Flush();
    }

    private static bool IsBackend(string value) => value is "CPU" or "CUDA" or "Vulkan0";

    public void Synthesize(BinaryReader reader, BinaryWriter writer)
    {
        if (!_loaded)
        {
            throw new InvalidOperationException("Qwen is not loaded.");
        }

        string text = reader.ReadString();
        string language = reader.ReadString();

        if (text.Length == 0 || text.Length > 2000 || !SupportedLanguage(language) || text.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("Invalid Qwen speech request.");
        }

        int referenceLength = reader.ReadInt32();
        float[] reference;
        if (referenceLength == 0 && _protocol == 2)
        {
            reference = _reference ?? throw new InvalidDataException("Missing Qwen voice reference.");
        }
        else
        {
            reference = ReadReference(reader, referenceLength);
            _reference = reference;
        }

        try
        {
            Respond(writer, text, language, reference);
        }
        catch (Exception ex)
        {
            // Never take the worker down over a single line: the game disables speech
            // for the whole session when the worker exits with an error reply.
            Log.Error("unexpected synthesis failure: " + ex);

            try
            {
                var sink = new SampleSink(SampleSink.Frames(writer), SpeechEngine.MaxSamples);
                sink.PadSilence(SpeechEngine.SilencePadSamples);
                sink.Flush();
                writer.Write(3);
                writer.Flush();
            }
            catch
            {
                // The game closed the pipe.
            }
        }
    }

    private static float[] ReadReference(BinaryReader reader, int length)
    {
        if (length < 2400 || length > 240000)
        {
            throw new InvalidDataException("Invalid Qwen reference length.");
        }

        byte[] raw = reader.ReadBytes(length * 4);
        if (raw.Length != length * 4)
        {
            throw new EndOfStreamException();
        }

        float[] reference = new float[length];
        Buffer.BlockCopy(raw, 0, reference, 0, raw.Length);
        for (int i = 0; i < length; i++)
        {
            if (float.IsNaN(reference[i]) || float.IsInfinity(reference[i]))
            {
                throw new InvalidDataException("Invalid reference sample.");
            }
        }

        return reference;
    }

    private void Respond(BinaryWriter writer, string text, string language, float[] reference)
    {
        Action<float[]> frames = SampleSink.Frames(writer);
        if (_passthroughOnly)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (TryPassthrough(text, language, reference, writer))
            {
                Log.Info($"passthrough: {text.Length} chars in {clock.ElapsedMilliseconds} ms");
                return;
            }

            var sink = new SampleSink(frames, SpeechEngine.MaxSamples);
            sink.PadSilence(SpeechEngine.SilencePadSamples);
            sink.Flush();
            Log.Warn($"no audio produced for {text.Length} chars; sending {SpeechEngine.SilencePadSamples} silent samples");
        }
        else
        {
            if (_engine.Speak(text, language, reference, frames,
                    _passthrough != null ? () => TryPassthrough(text, language, reference, writer) : null))
            {
                return;
            }
        }

        writer.Write(3);
        writer.Flush();
    }

    /// The original worker writes its own end marker.
    private bool TryPassthrough(string text, string language, float[] reference, BinaryWriter writer)
        => _passthrough != null && _passthrough.TrySynthesize(text, language, reference, writer);

    private static bool SupportedLanguage(string language) => language switch
    {
        "English" or "Chinese" or "Japanese" or "Korean" or "German" or "French" or "Russian"
            or "Portuguese" or "Spanish" or "Italian" => true,
        _ => false,
    };

    public void Dispose()
    {
        _engine.Dispose();
        _passthrough?.Dispose();
    }
}
