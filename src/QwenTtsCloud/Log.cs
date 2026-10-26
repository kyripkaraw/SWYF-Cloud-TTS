namespace SwyfCloudTts;

internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _verbose;

    public static void Init(string? directory, bool verbose)
    {
        _verbose = verbose;
        try
        {
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
                _path = Path.Combine(directory, "worker.log");
                if (File.Exists(_path) && new FileInfo(_path).Length > 4 * 1024 * 1024)
                {
                    Compat.MoveOverwrite(_path, _path + ".old");
                }
            }
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Debug(string message)
    {
        if (_verbose)
        {
            Write("DEBUG", message);
        }
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                Console.Error.WriteLine(line);
            }
            catch
            {
                // The game may have closed stderr.
            }

            try
            {
                if (_path != null)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break synthesis.
            }
        }
    }
}
