namespace SwyfCloudTts;

/// Collects float32 audio into chunks of the size the game expects and hands each chunk to
/// the output: the worker frames it for the pipe, the in-game plugin passes it straight to the
/// game's own callback.
internal sealed class SampleSink
{
    private readonly Action<float[]> _output;
    private readonly int _maxSamples;
    private readonly float[] _buffer;
    private readonly MemoryStream? _record;
    private int _buffered;

    public SampleSink(Action<float[]> output, int maxSamples, int chunkSamples = 4800, bool record = false)
    {
        _output = output;
        _maxSamples = maxSamples;
        _buffer = new float[chunkSamples];
        _record = record ? new MemoryStream() : null;
    }

    public int Total { get; private set; }

    public bool Capped => Total >= _maxSamples;

    public byte[]? RecordedS16 => _record?.ToArray();

    public void Add(float[] samples)
    {
        foreach (float sample in samples)
        {
            Add(sample);
            if (Capped)
            {
                return;
            }
        }
    }

    public void Add(float sample)
    {
        if (Total >= _maxSamples)
        {
            return;
        }

        float value = float.IsNaN(sample) || float.IsInfinity(sample) ? 0f : Pcm.Clamp(sample);
        _buffer[_buffered++] = value;
        if (_buffered == _buffer.Length)
        {
            Emit(_buffer.Length);
        }
    }

    public void Flush()
    {
        if (_buffered > 0)
        {
            Emit(_buffered);
        }
    }

    /// Adds silence so a partially failed cloud request still ends with a well formed stream.
    public void PadSilence(int samples)
    {
        for (int i = 0; i < samples && !Capped; i++)
        {
            Add(0f);
        }
    }

    /// Replays already encoded 16-bit audio (used by the disk cache) in protocol chunks.
    public static int PumpS16(byte[] s16, int maxSamples, Action<float[]> output, int chunkSamples = 4800)
    {
        int samples = Math.Min(s16.Length / 2, maxSamples);
        int written = 0;
        while (written < samples)
        {
            int count = Math.Min(chunkSamples, samples - written);
            var floats = new float[count];
            for (int i = 0; i < count; i++)
            {
                floats[i] = Pcm.ReadInt16(s16, (written + i) * 2) / 32768f;
            }

            output(floats);
            written += count;
        }

        return written;
    }

    /// The worker's pipe format: int32 marker 2, int32 sample count, count * float32 little-endian.
    public static Action<float[]> Frames(BinaryWriter writer) => samples =>
    {
        writer.Write(2);
        writer.Write(samples.Length);
        writer.Write(Pcm.FloatToBytes(samples, samples.Length));
        writer.Flush();
    };

    private void Emit(int count)
    {
        if (count <= 0)
        {
            return;
        }

        int allowed = Math.Min(count, _maxSamples - Total);
        if (allowed <= 0)
        {
            _buffered = 0;
            return;
        }

        if (_record != null)
        {
            byte[] s16 = Pcm.FloatToS16(_buffer, allowed);
            _record.Write(s16, 0, s16.Length);
        }

        _output(Compat.Slice(_buffer, 0, allowed));

        Total += allowed;

        int leftover = count - allowed;
        if (leftover > 0)
        {
            Array.Copy(_buffer, allowed, _buffer, 0, leftover);
        }

        _buffered = leftover;
    }
}
