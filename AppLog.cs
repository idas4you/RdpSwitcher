namespace RdpSwitcher;

internal static class AppLog
{
    private static readonly object SyncRoot = new();
    private const string LogFileNamePrefix = "RdpSwitcher";

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RdpSwitcher");

    public static string LogPath => GetLogPath(DateTimeOffset.Now);

    public static void Write(string message)
    {
        try
        {
            var now = DateTimeOffset.Now;
            Directory.CreateDirectory(LogDirectory);
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(GetLogPath(now), line);
            }
        }
        catch
        {
        }
    }

    private static string GetLogPath(DateTimeOffset timestamp)
    {
        return Path.Combine(LogDirectory, $"{LogFileNamePrefix}-{timestamp:yyyy-MM-dd}.log");
    }
}
