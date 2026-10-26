using System.Text;

namespace SwyfCloudTts;

/// Disk cache of synthesized lines. The game splits speech into ~240 byte chunks, so
/// recurring barks, greetings and combat lines are requested over and over again.
/// Caching them makes the cloud almost free and removes most of the latency.
internal sealed class AudioCache
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytes;

    public AudioCache(Config config)
    {
        Enabled = config.Cache.Enabled;
        _maxBytes = Math.Max(0, config.Cache.MaxMegabytes) * 1024L * 1024L;
        _directory = Path.Combine(config.StateDirectory, "cache");

        if (!Enabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch (Exception ex)
        {
            Log.Warn("cache directory unavailable, caching disabled: " + ex.Message);
            Enabled = false;
        }
    }

    public bool Enabled { get; private set; }

    public static string Key(params string?[] parts)
    {
        // Unit separator cannot appear in text/voice ids, so the key stays unambiguous.
        string joined = string.Join("\u001f", parts.Select(p => p ?? ""));
        return Compat.Sha256Hex(Encoding.UTF8.GetBytes(joined));
    }

    public bool TryGet(string key, out byte[] samples)
    {
        samples = Array.Empty<byte>();

        if (!Enabled)
        {
            return false;
        }

        string path = PathFor(key);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            samples = File.ReadAllBytes(path);
            if (samples.Length < 2)
            {
                samples = Array.Empty<byte>();
                return false;
            }

            // Refresh the timestamp so pruning behaves like LRU.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"cache read failed ({key}): {ex.Message}");
            return false;
        }
    }

    public void Put(string key, byte[] samples)
    {
        if (!Enabled || samples.Length < 2 || samples.Length > 4 * 1024 * 1024)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                string path = PathFor(key);
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, samples);
                Compat.MoveOverwrite(temp, path);
                Prune();
            }
            catch (Exception ex)
            {
                Log.Debug($"cache write failed ({key}): {ex.Message}");
            }
        }
    }

    private string PathFor(string key) => Path.Combine(_directory, key + ".pcm");

    private void Prune()
    {
        if (_maxBytes <= 0)
        {
            return;
        }

        var files = new DirectoryInfo(_directory).GetFiles("*.pcm");
        long total = files.Sum(f => f.Length);
        if (total <= _maxBytes)
        {
            return;
        }

        long target = (long)(_maxBytes * 0.9);
        foreach (FileInfo file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= target)
            {
                break;
            }

            try
            {
                long length = file.Length;
                file.Delete();
                total -= length;
            }
            catch
            {
                // Still open in another process; pruned next time.
            }
        }
    }
}
