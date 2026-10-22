using System.Buffers.Binary;

namespace SwyfCloudTts;

/// <summary>
/// Writes float32 audio to the game in the exact frame format QwenWorkerClient expects:
/// int32 marker 2, int32 sample count, count * float32 little-endian samples.
/// </summary>
internal sealed class SampleSink
{
    private readonly BinaryWriter _writer;
    private readonly int _maxSamples;
    private readonly float[] _buffer;
    private readonly MemoryStream? _record;
    private int _buffered;

    public SampleSink(BinaryWriter writer, int maxSamples, int chunkSamples = 4800, bool record = false)
    {
        _writer = writer;
        _maxSamples = maxSamples;
        _buffer = new float[chunkSamples];
        _record = record ? new MemoryStream() : null;
    }

    public int Total { get; private set; }

    public bool Capped => Total >= _maxSamples;

    public byte[]? RecordedS16 => _record?.ToArray();

    public void Add(ReadOnlySpan<float> samples)
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

        float value = float.IsNaN(sample) || float.IsInfinity(sample) ? 0f : Math.Clamp(sample, -1f, 1f);
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

    /// <summary>Adds silence so a partially failed cloud request still ends with a well formed stream.</summary>
    public void PadSilence(int samples)
    {
        for (int i = 0; i < samples && !Capped; i++)
        {
            Add(0f);
        }
    }

    /// <summary>Replays already encoded 16-bit audio (used by the disk cache) in protocol chunks.</summary>
    public static int PumpS16(BinaryWriter writer, ReadOnlySpan<byte> s16, int maxSamples, int chunkSamples = 4800)
    {
        int samples = Math.Min(s16.Length / 2, maxSamples);
        int written = 0;
        while (written < samples)
        {
            int count = Math.Min(chunkSamples, samples - written);
            ReadOnlySpan<byte> chunk = s16.Slice(written * 2, count * 2);
            var floats = new float[count];
            for (int i = 0; i < count; i++)
            {
                floats[i] = BinaryPrimitives.ReadInt16LittleEndian(chunk[(i * 2)..]) / 32768f;
            }

            writer.Write(2);
            writer.Write(count);
            writer.Write(Pcm.FloatToBytes(floats));
            writer.Flush();
            written += count;
        }

        return written;
    }

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

        _record?.Write(Pcm.FloatToS16(_buffer.AsSpan(0, allowed)));

        _writer.Write(2);
        _writer.Write(allowed);
        _writer.Write(Pcm.FloatToBytes(_buffer.AsSpan(0, allowed)));
        _writer.Flush();

        Total += allowed;

        int leftover = count - allowed;
        if (leftover > 0)
        {
            Array.Copy(_buffer, allowed, _buffer, 0, leftover);
        }

        _buffered = leftover;
    }
}
