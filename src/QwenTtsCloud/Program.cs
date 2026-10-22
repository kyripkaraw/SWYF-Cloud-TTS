using System.Text;

namespace SwyfCloudTts;

/// Drop-in replacement for the game's QwenTtsWorker.exe. Speaks the same binary protocol on
/// stdin/stdout, but synthesizes speech through a cloud provider instead of the local model.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--test":
                    Cli.EnableUtf8Console();
                    return TestRunner.Run(args);
                case "--print-config":
                    Cli.EnableUtf8Console();
                    return PrintConfig(args);
                case "--list-voices":
                    Cli.EnableUtf8Console();
                    return ListVoices(args);
                case "--version":
                    Console.WriteLine(Config.Version);
                    return 0;
                case "--help":
                case "-h":
                case "/?":
                    PrintHelp();
                    return 0;
            }
        }

        Log.Info("QwenTtsWorker (cloud) starting; args=" + string.Join(' ', args));

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var reader = new BinaryReader(input, new UTF8Encoding(false), leaveOpen: true);
        using var writer = new BinaryWriter(output, new UTF8Encoding(false), leaveOpen: true);

        try
        {
            using App app = App.Create();

            while (true)
            {
                int command;
                try
                {
                    command = reader.ReadInt32();
                }
                catch (EndOfStreamException)
                {
                    Log.Info("stdin closed, exiting.");
                    return 0;
                }

                switch (command)
                {
                    case 1:
                        app.Load(reader, writer);
                        break;
                    case 2:
                        app.Synthesize(reader, writer);
                        break;
                    default:
                        throw new InvalidDataException("Unknown Qwen command.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("fatal: " + ex);
            try
            {
                writer.Write(4);
                writer.Write(ex.Message);
                writer.Flush();
            }
            catch
            {
                // The game has already closed the pipe.
            }

            return 1;
        }
    }

    private static int PrintConfig(string[] args)
    {
        Config config = Config.Load(Cli.Value(args, "--config"));

        Console.WriteLine($"version:   {Config.Version}");
        Console.WriteLine($"config:    {config.Path} ({(File.Exists(config.Path) ? "present" : "missing")})");
        Console.WriteLine($"state:     {config.StateDirectory}");
        Console.WriteLine($"provider:  {config.Provider}");
        Console.WriteLine($"openai:    key={Mask(config.OpenAi.ApiKey)} model={config.OpenAi.Model} voice={config.OpenAi.Voice ?? "(auto)"}");
        Console.WriteLine($"elevenlabs:key={Mask(config.ElevenLabs.ApiKey)} model={config.ElevenLabs.ModelId} " +
                          $"autoClone={config.ElevenLabs.AutoClone} voice={config.ElevenLabs.DefaultVoiceId}");
        Console.WriteLine($"googleai:  key={Mask(config.GoogleAi.ApiKey)} model={config.GoogleAi.Model} " +
                          $"voice={(config.GoogleAi.VoiceId.Length > 0 ? config.GoogleAi.VoiceId : "(auto)")}");
        Console.WriteLine($"qwenremote:url={config.QwenRemote.BaseUrl} mode={config.QwenRemote.Mode} " +
                          $"key={Mask(config.QwenRemote.ApiKey)}");
        Console.WriteLine($"network:   timeout={config.Network.TimeoutSeconds}s attempts={config.Network.MaxAttempts}");
        Console.WriteLine($"cache:     enabled={config.Cache.Enabled} max={config.Cache.MaxMegabytes} MB");
        return 0;
    }

    /// Saves the ElevenLabs account's own voices for the settings card.
    private static int ListVoices(string[] args)
    {
        Config config = Config.Load(Cli.Value(args, "--config"));
        string? key = Cli.Value(args, "--key");
        if (!string.IsNullOrWhiteSpace(key))
        {
            config.ElevenLabs.ApiKey = key!;
        }

        if (string.IsNullOrWhiteSpace(config.ElevenLabs.ApiKey))
        {
            Console.Error.WriteLine("no ElevenLabs API key; set elevenlabs.apiKey or SWYF_TTS_ELEVENLABS_KEY");
            return 2;
        }

        try
        {
            using var provider = new Providers.ElevenLabsTts(config, new VoiceResolver(config));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            int count = provider.SaveAccountVoicesAsync(timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine($"saved {count} voice(s) to elevenlabs-voices.json");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string Mask(string key)
        => key.Length == 0 ? "(not set)" : "****" + key[^Math.Min(4, key.Length)..];

    private static void PrintHelp()
    {
        Console.WriteLine($"SWYF Cloud TTS worker v{Config.Version}");
        Console.WriteLine();
        Console.WriteLine("  QwenTtsWorker.exe                 worker mode (the game starts it like this)");
        Console.WriteLine("  QwenTtsWorker.exe --test          synthesize one sentence to a wav file");
        Console.WriteLine("  QwenTtsWorker.exe --print-config  show the active config with masked keys");
        Console.WriteLine("  QwenTtsWorker.exe --list-voices   save the ElevenLabs account's own voices for the card");
        Console.WriteLine("  QwenTtsWorker.exe --version       print the version and exit");
        Console.WriteLine();
        Console.WriteLine("Test options:");
        Console.WriteLine("  --text <text>     phrase to speak (default: a short Russian greeting)");
        Console.WriteLine("  --out <path>      wav output (default: cloud-tts-test.wav)");
        Console.WriteLine("  --provider <name> googleai | elevenlabs | openai | qwenremote | mock | silent");
        Console.WriteLine("  --voice <id>      provider voice id");
        Console.WriteLine("  --speaker <id>    use this character's saved game clip (cloning tests)");
        Console.WriteLine("  --model <id>      provider model id");
        Console.WriteLine("  --key <key>       API key for this run only");
        Console.WriteLine("  --base-url <url>  qwenremote server root, for example http://127.0.0.1:8000");
        Console.WriteLine("  --mode <mode>     qwenremote: clone | voice | design");
        Console.WriteLine("  --language <name> language name sent to the provider (default: Russian)");
        Console.WriteLine("  --config <path>   use another config file");
    }
}
