using System.Diagnostics;

namespace SwyfCloudTts.Providers;

/// <summary>
/// Optional safety net: forwards a request to the untouched original worker
/// (renamed to QwenTtsWorker.orig.exe by the installer).
/// It is started lazily and only when the cloud provider fails, because loading the
/// 946 MB GGUF model takes a long time.
/// </summary>
internal sealed class PassthroughClient : IDisposable
{
    private readonly string _executable;
    private Process? _process;
    private BinaryReader? _input;
    private BinaryWriter? _output;

    private string _talker = "";
    private string _codec = "";
    private string _backend = "CPU";
    private bool _loadRequested;

    public PassthroughClient(Config config)
    {
        _executable = Path.Combine(AppContext.BaseDirectory, config.Fallback.OriginalExe);
    }

    public bool Available => File.Exists(_executable);

    public void RememberLoad(string talker, string codec, string backend)
    {
        _talker = talker;
        _codec = codec;
        _backend = backend;
    }

    /// <summary>
    /// Returns true when the whole reply (chunks plus terminator) was already written
    /// to the game. Returns false when nothing usable was produced.
    /// </summary>
    public bool TrySynthesize(string text, string language, float[] reference, BinaryWriter writer)
    {
        try
        {
            EnsureStarted();

            byte[] raw = new byte[reference.Length * 4];
            Buffer.BlockCopy(reference, 0, raw, 0, raw.Length);

            _output!.Write(2);
            _output.Write(text);
            _output.Write(language);
            _output.Write(reference.Length);
            _output.Write(raw);
            _output.Flush();

            int total = 0;
            while (true)
            {
                int code = _input!.ReadInt32();
                switch (code)
                {
                    case 2:
                    {
                        int count = _input.ReadInt32();
                        if (count <= 0 || count > 720000)
                        {
                            throw new InvalidDataException("local worker sent an invalid chunk size");
                        }

                        byte[] payload = _input.ReadBytes(count * 4);
                        if (payload.Length != count * 4)
                        {
                            throw new EndOfStreamException("local worker exited mid-chunk");
                        }

                        writer.Write(2);
                        writer.Write(count);
                        writer.Write(payload);
                        writer.Flush();
                        total += count;
                        break;
                    }
                    case 3:
                        if (total == 0)
                        {
                            Log.Warn("passthrough produced no audio");
                            return false;
                        }

                        writer.Write(3);
                        writer.Flush();
                        return true;
                    case 4:
                        Log.Warn("passthrough error: " + _input.ReadString());
                        return false;
                    default:
                        throw new InvalidDataException($"unexpected local worker reply {code}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("passthrough failed: " + ex.Message);
            Dispose();
            return false;
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            if (!_loadRequested)
            {
                SendLoad();
            }

            return;
        }

        Dispose();

        Log.Info("starting original worker for passthrough: " + _executable);
        var info = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process = Process.Start(info) ?? throw new IOException("original worker did not start");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Log.Debug("[orig] " + e.Data);
            }
        };
        _process.BeginErrorReadLine();

        _input = new BinaryReader(_process.StandardOutput.BaseStream);
        _output = new BinaryWriter(_process.StandardInput.BaseStream);
        SendLoad();
    }

    private void SendLoad()
    {
        Log.Info("passthrough load: waiting for the local model (this can take a while)");
        _output!.Write(1);
        _output.Write(_talker);
        _output.Write(_codec);
        _output.Write(_backend);
        _output.Flush();

        int code = _input!.ReadInt32();
        if (code == 4)
        {
            throw new IOException("original worker load failed: " + _input.ReadString());
        }

        if (code != 1 || _input.ReadInt32() != 1)
        {
            throw new InvalidDataException("original worker rejected the load request");
        }

        _loadRequested = true;
        Log.Info("passthrough ready");
    }

    public void Dispose()
    {
        _loadRequested = false;
        try
        {
            _input?.Dispose();
            _output?.Dispose();
        }
        catch
        {
            // The pipe is already broken.
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The local worker has already exited.
        }

        _process?.Dispose();
        _process = null;
        _input = null;
        _output = null;
    }
}
