using System.Text;

namespace SwyfCloudTts;

/// PCM helpers. Everything in this mod is 24 kHz mono float32, like the game expects.
internal static class Pcm
{
    public const int SampleRate = 24000;

    /// Converts little-endian signed 16-bit PCM into float samples in [-1, 1].
    internal sealed class S16ToFloat
    {
        private byte _leftover;
        private bool _hasLeftover;

        /// Returns the samples completed by this batch (may be empty).
        public float[] Push(byte[] bytes, int offset, int length)
        {
            int start = offset;
            int end = offset + length;
            int count = (length + (_hasLeftover ? 1 : 0)) / 2;
            if (count == 0)
            {
                if (length == 1)
                {
                    _leftover = bytes[offset];
                    _hasLeftover = true;
                }

                return Array.Empty<float>();
            }

            var result = new float[count];
            int index = 0;

            if (_hasLeftover)
            {
                ushort value = (ushort)(_leftover | (bytes[start] << 8));
                result[index++] = (short)value / 32768f;
                _hasLeftover = false;
                start++;
            }

            int usableEnd = start + ((end - start) & ~1);
            for (int i = start; i < usableEnd; i += 2)
            {
                result[index++] = ReadInt16(bytes, i) / 32768f;
            }

            if (usableEnd < end)
            {
                _leftover = bytes[usableEnd];
                _hasLeftover = true;
            }

            return index == result.Length ? result : Compat.Slice(result, 0, index);
        }

        /// Pads an odd trailing byte so a stream never ends on half a sample.
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

    /// Encodes float samples as little-endian 32-bit floats, the format the game reads.
    public static byte[] FloatToBytes(float[] samples, int count)
    {
        var clean = new float[count];
        for (int i = 0; i < count; i++)
        {
            float v = samples[i];
            clean[i] = float.IsNaN(v) || float.IsInfinity(v) ? 0f : Clamp(v);
        }

        var bytes = new byte[count * 4];
        Buffer.BlockCopy(clean, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static byte[] FloatToS16(float[] samples) => FloatToS16(samples, samples.Length);

    public static byte[] FloatToS16(float[] samples, int count)
    {
        var bytes = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            float v = samples[i];
            if (float.IsNaN(v))
            {
                v = 0f;
            }

            short s = (short)Math.Round(Clamp(v) * 32767f);
            bytes[i * 2] = (byte)s;
            bytes[i * 2 + 1] = (byte)(s >> 8);
        }

        return bytes;
    }

    public static float Clamp(float value) => value < -1f ? -1f : (value > 1f ? 1f : value);

    public static short ReadInt16(byte[] data, int offset) => (short)(data[offset] | (data[offset + 1] << 8));

    /// Stable id for a speaker reference; used to remember which cloud voice belongs to whom.
    public static string HashReference(float[] reference)
    {
        // Quantize to 16 bit so tiny floating point differences don't change the id.
        return Compat.Sha256Hex(FloatToS16(reference), 8);
    }

    /// Very rough median pitch estimate, used only to pick a plausible cloud voice.
    public static double EstimatePitchHz(float[] samples, int sampleRate = SampleRate)
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

    /// Decodes a RIFF/WAVE byte array (16-bit PCM, 24-bit PCM, 8-bit PCM or 32-bit float,
    /// any channel count) into 24 kHz mono float samples. Providers that cannot stream raw
    /// PCM at 24 kHz are normalised here.
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
            int size = BitConverter.ToInt32(data, pos + 4);
            int body = pos + 8;

            if (id == "fmt " && body + 16 <= data.Length)
            {
                format = ReadInt16(data, body);
                channels = Math.Max(1, (int)ReadInt16(data, body + 2));
                sampleRate = BitConverter.ToInt32(data, body + 4);
                bits = Math.Max(8, (int)ReadInt16(data, body + 14));

                if (format == 0xFFFE && body + 26 <= data.Length)
                {
                    // WAVE_FORMAT_EXTENSIBLE stores the real format in the GUID prefix.
                    format = ReadInt16(data, body + 24);
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
                    sum += BitConverter.ToSingle(data, offset);
                }
                else if (bits == 8)
                {
                    sum += (data[offset] - 128) / 128.0;
                }
                else if (bits == 16)
                {
                    sum += ReadInt16(data, offset) / 32768.0;
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
                    sum += BitConverter.ToInt32(data, offset) / 2147483648.0;
                }
            }

            mono[frame] = (float)(sum / channels);
        }

        return Resample(mono, sampleRate, SampleRate);
    }

    /// Cheap linear resampler. Speech quality is fine and the cost is negligible.
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
            output[i] = Clamp((float)(a + (b - a) * fraction));
        }

        return output;
    }

    /// Wraps float samples into a minimal 16-bit mono WAV file (used for voice cloning).
    public static byte[] ToWav(float[] samples, int sampleRate = SampleRate)
    {
        byte[] pcm = FloatToS16(samples);
        using var ms = new MemoryStream(44 + pcm.Length);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm);
        bw.Flush();
        return ms.ToArray();
    }
}
