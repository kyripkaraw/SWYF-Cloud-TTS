using System.Security.Cryptography;

namespace SwyfCloudTts;

/// The same sources build the .NET 8 worker and the in-game BepInEx plugin, which runs on the
/// game's Mono class libraries. These helpers stand in for the newer APIs Mono lacks.
internal static class Compat
{
    public static void MoveOverwrite(string source, string destination)
    {
#if NETFRAMEWORK
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(source, destination);
#else
        File.Move(source, destination, overwrite: true);
#endif
    }

    /// Lowercase hex of the first <paramref name="bytes"/> bytes of the SHA-256 of the data.
    public static string Sha256Hex(byte[] data, int bytes = 32)
    {
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(data);
        }

        return Hex(hash, Math.Min(bytes, hash.Length));
    }

    public static string Hex(byte[] data, int count)
    {
        var builder = new System.Text.StringBuilder(count * 2);
        for (int i = 0; i < count; i++)
        {
            builder.Append(data[i].ToString("x2"));
        }

        return builder.ToString();
    }

    public static float[] Slice(float[] data, int start, int length)
    {
        var result = new float[length];
        Array.Copy(data, start, result, 0, length);
        return result;
    }
}
