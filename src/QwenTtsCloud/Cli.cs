namespace SwyfCloudTts;

/// Tiny argument helper shared by the CLI modes. Accepts "--name value" and "--name=value".
internal static class Cli
{
    /// UTF-8 only for the console tools; the worker's stdout must stay binary.
    public static void EnableUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        }
        catch
        {
            // Redirected output has no console encoding to set.
        }
    }

    public static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : "";
            }

            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(name.Length + 1)..];
            }
        }

        return null;
    }

    public static bool Flag(string[] args, string name)
        => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}
