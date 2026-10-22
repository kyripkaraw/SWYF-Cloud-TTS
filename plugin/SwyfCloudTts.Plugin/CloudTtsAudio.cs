using System;
using System.Text;
using UnityEngine;

namespace SwyfCloudTts.Plugin
{
    /// <summary>
    /// Plays the 16-bit WAV files the worker writes (test lines and saved game clips) on a
    /// separate AudioSource. Float WAV is not needed: the game's mscorlib has no BitConverter.ToSingle.
    /// </summary>
    internal static class CloudTtsAudio
    {
        private static AudioSource _current;

        public static string Play(byte[] wav)
        {
            if (wav == null || wav.Length < 44)
            {
                return "empty WAV";
            }

            try
            {
                float[] samples;
                int sampleRate;
                string error = Parse(wav, out samples, out sampleRate);
                if (error != null)
                {
                    return error;
                }

                if (_current != null)
                {
                    try
                    {
                        _current.Stop();
                        UnityEngine.Object.Destroy(_current.gameObject);
                    }
                    catch
                    {
                        // The previous clip is already destroyed.
                    }

                    _current = null;
                }

                var clip = AudioClip.Create("SWYF Cloud TTS Test", samples.Length, 1, sampleRate, false);
                if (clip == null)
                {
                    return "could not create an AudioClip";
                }

                clip.SetData(samples, 0);

                var go = new GameObject("SWYF Cloud TTS Test");
                var source = go.AddComponent<AudioSource>();
                source.clip = clip;
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.volume = 1f;
                source.ignoreListenerPause = true;
                source.Play();
                _current = source;

                UnityEngine.Object.Destroy(go, Math.Max(1f, clip.length + 1f));
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string Parse(byte[] wav, out float[] samples, out int sampleRate)
        {
            samples = Array.Empty<float>();
            sampleRate = 24000;

            if (wav.Length < 12 ||
                Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            {
                return "not a RIFF/WAVE file";
            }

            int format = 0;
            int channels = 0;
            int bits = 0;
            int dataOffset = -1;
            int dataLength = 0;
            int position = 12;

            while (position + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, position, 4);
                int size = BitConverter.ToInt32(wav, position + 4);
                int body = position + 8;

                if (id == "fmt " && body + 16 <= wav.Length)
                {
                    format = BitConverter.ToUInt16(wav, body);
                    channels = BitConverter.ToUInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bits = BitConverter.ToUInt16(wav, body + 14);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = Math.Min(size, wav.Length - body);
                    break;
                }

                if (size < 0)
                {
                    break;
                }

                position = body + size + (size & 1);
            }

            if (dataOffset < 0 || dataLength <= 0)
            {
                return "WAV has no data chunk";
            }

            if (channels <= 0 || channels > 8 || sampleRate <= 0 || sampleRate > 192000)
            {
                return "unsupported WAV format";
            }

            if (format != 1)
            {
                return "unsupported WAV encoding";
            }

            if (bits != 8 && bits != 16 && bits != 24 && bits != 32)
            {
                return "unsupported PCM bit depth";
            }

            int bytesPerSample = bits / 8;
            int frameSize = bytesPerSample * channels;
            if (frameSize <= 0)
            {
                return "invalid WAV frame size";
            }

            int frames = dataLength / frameSize;
            if (frames <= 0)
            {
                return "WAV has no samples";
            }

            samples = new float[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                int offset = dataOffset + frame * frameSize;
                float sum = 0f;

                for (int channel = 0; channel < channels; channel++)
                {
                    int at = offset + channel * bytesPerSample;
                    sum += ReadSample(wav, at, bits);
                }

                samples[frame] = Mathf.Clamp(sum / channels, -1f, 1f);
            }

            return null;
        }

        private static float ReadSample(byte[] data, int offset, int bits)
        {
            switch (bits)
            {
                case 8:
                    return (data[offset] - 128) / 128f;
                case 16:
                    return BitConverter.ToInt16(data, offset) / 32768f;
                case 24:
                    int sample24 = data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
                    if ((sample24 & 0x800000) != 0)
                    {
                        sample24 |= unchecked((int)0xFF000000);
                    }

                    return sample24 / 8388608f;
                case 32:
                    return BitConverter.ToInt32(data, offset) / 2147483648f;
                default:
                    return 0f;
            }
        }
    }
}
