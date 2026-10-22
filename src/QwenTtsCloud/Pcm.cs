using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SwyfCloudTts;

/// <summary>PCM helpers. Everything in this mod is 24 kHz mono float32, like the game expects.</summary>
internal static class Pcm
{
    public const int SampleRate = 24000;

    /// <summary>Converts little-endian signed 16-bit PCM into float samples in [-1, 1].</summary>
    internal sealed class S16ToFloat
    {
        private byte _leftover;
        private bool _hasLeftover;

        /// <summary>Returns the samples completed by this batch (may be empty).</summary>
        public float[] Push(ReadOnlySpan<byte> bytes)
        {
            int start = 0;
            int count = (bytes.Length + (_hasLeftover ? 1 : 0)) / 2;
            if (count == 0)
            {
                if (bytes.Length == 1)
                {
                    _leftover = bytes[0];
                    _hasLeftover = true;
                }

                return Array.Empty<float>();
            }

            var result = new float[count];
            int index = 0;

            if (_hasLeftover)
            {
                ushort value = (ushort)(_leftover | (bytes[0] << 8));
                result[index++] = (short)value / 32768f;
                _hasLeftover = false;
                start = 1;
            }

            int usable = (bytes.Length - start) & ~1;
            int end = start + usable;
            for (int offset = start; offset < end; offset += 2)
            {
                result[index++] = BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) / 32768f;
            }

            if (end < bytes.Length)
            {
                _leftover = bytes[end];
                _hasLeftover = true;
            }

            return index == result.Length ? result : result[..index];
        }

        /// <summary>Pads an odd trailing byte so a stream never ends on half a sample.</summary>
        public float[] Flush()
        {
            if (!_hasLeftover)
            {
                return Array.Empty<float>();
            }

            _hasLeftover = false;
            return new[] { (short)_leftover / 32768f };
        }
    }

    /// <summary>Encodes float samples as little-endian 32-bit floats, the format the game reads.</summary>
    public static byte[] FloatToBytes(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                v = 0f;
            }

            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), Math.Clamp(v, -1f, 1f));
        }

        return bytes;
    }

    public static byte[] FloatToS16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i];
            if (float.IsNaN(v))
            {
                v = 0f;
            }

            v = Math.Clamp(v, -1f, 1f);
            short s = (short)Math.Round(v * 32767f);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), s);
        }

        return bytes;
    }

    /// <summary>Stable id for a speaker reference; used to remember which cloud voice belongs to whom.</summary>
    public static string HashReference(ReadOnlySpan<float> reference)
    {
        // Quantize to 16 bit so tiny floating point differences don't change the id.
        byte[] bytes = FloatToS16(reference);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>Very rough median pitch estimate, used only to pick a plausible cloud voice.</summary>
    public static double EstimatePitchHz(ReadOnlySpan<float> samples, int sampleRate = SampleRate)
    {
        const int frame = 1024;
        const int hop = 512;
        const int window = SampleRate;
        int minLag = sampleRate / 400;
        int maxLag = sampleRate / 70;

        int limit = Math.Min(samples.Length, window + maxLag);
        var candidates = new List<double>();
        for (int start = 0; start + frame + maxLag < limit; start += hop)
        {
            double energy = 0;
            for (int i = 0; i < frame; i++)
            {
                energy += samples[start + i] * samples[start + i];
            }

            if (energy / frame < 1e-5)
            {
                continue;
            }

            double best = 0;
            int bestLag = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                double corr = 0;
                for (int i = 0; i < frame; i++)
                {
                    corr += samples[start + i] * samples[start + i + lag];
                }

                if (corr > best)
                {
                    best = corr;
                    bestLag = lag;
                }
            }

            if (bestLag > 0 && best > 0.3 * energy)
            {
                candidates.Add((double)sampleRate / bestLag);
            }
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        candidates.Sort();
        return candidates[candidates.Count / 2];
    }

    /// <summary>
    /// Decodes a RIFF/WAVE byte array (16-bit PCM, 24-bit PCM, 8-bit PCM or 32-bit float,
    /// any channel count) into 24 kHz mono float samples. Providers that cannot stream raw
    /// PCM at 24 kHz are normalised here.
    /// </summary>
    public static float[] FromWav(byte[] data)
    {
        if (data == null || data.Length < 12 ||
            data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' ||
            data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
        {
            throw new InvalidDataException("response is not a RIFF/WAVE file");
        }

        int format = 1;
        int channels = 1;
        int sampleRate = SampleRate;
        int bits = 16;
        int dataOffset = -1;
        int dataLength = 0;

        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos + 4, 4));
            int body = pos + 8;

            if (id == "fmt " && body + 16 <= data.Length)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body, 2));
                channels = Math.Max(1, (int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 2, 2)));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(body + 4, 4));
                bits = Math.Max(8, (int)BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 14, 2)));

                if (format == 0xFFFE && body + 26 <= data.Length)
                {
                    // WAVE_FORMAT_EXTENSIBLE stores the real format in the GUID prefix.
                    format = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(body + 24, 2));
                }
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = size <= 0 || body + size > data.Length ? data.Length - body : size;
                break;
            }

            if (size < 0)
            {
                break;
            }

            pos = body + size + (size & 1);
        }

        if (dataOffset < 0 || dataLength <= 0)
        {
            throw new InvalidDataException("WAV file has no audio data");
        }

        if (sampleRate <= 0)
        {
            sampleRate = SampleRate;
        }

        int bytesPerSample = Math.Max(1, bits / 8);
        int frameBytes = bytesPerSample * channels;
        int frames = dataLength / frameBytes;
        if (frames <= 0)
        {
            return Array.Empty<float>();
        }

        var mono = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            int frameOffset = dataOffset + frame * frameBytes;
            double sum = 0;

            for (int channel = 0; channel < channels; channel++)
            {
                int offset = frameOffset + channel * bytesPerSample;
                if (offset + bytesPerSample > data.Length)
                {
                    break;
                }

                if (format == 3 && bits == 32)
                {
                    sum += BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));
                }
                else if (bits == 8)
                {
                    sum += (data[offset] - 128) / 128.0;
                }
                else if (bits == 16)
                {
                    sum += BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) / 32768.0;
                }
                else if (bits == 24)
                {
                    int value = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
                    if ((value & 0x800000) != 0)
                    {
                        value |= unchecked((int)0xFF000000);
                    }

                    sum += value / 8388608.0;
                }
                else
                {
                    sum += BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)) / 2147483648.0;
                }
            }

            mono[frame] = (float)(sum / channels);
        }

        return Resample(mono, sampleRate, SampleRate);
    }

    /// <summary>Cheap linear resampler. Speech quality is fine and the cost is negligible.</summary>
    public static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (input.Length == 0 || fromRate <= 0 || toRate <= 0 || fromRate == toRate)
        {
            return input;
        }

        int length = (int)Math.Round(input.Length * (double)toRate / fromRate);
        if (length <= 0)
        {
            return Array.Empty<float>();
        }

        var output = new float[length];
        double step = (double)fromRate / toRate;
        for (int i = 0; i < length; i++)
        {
            double position = i * step;
            int index = (int)position;
            double fraction = position - index;
            float a = input[Math.Min(index, input.Length - 1)];
            float b = input[Math.Min(index + 1, input.Length - 1)];
            output[i] = Math.Clamp((float)(a + (b - a) * fraction), -1f, 1f);
        }

        return output;
    }

    /// <summary>Wraps float samples into a minimal 16-bit mono WAV file (used for voice cloning).</summary>
    public static byte[] ToWav(ReadOnlySpan<float> samples, int sampleRate = SampleRate)
    {
        byte[] pcm = FloatToS16(samples);
        using var ms = new MemoryStream(44 + pcm.Length);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write("RIFF"u8);
        bw.Write(36 + pcm.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(pcm.Length);
        bw.Write(pcm);
        bw.Flush();
        return ms.ToArray();
    }
}
