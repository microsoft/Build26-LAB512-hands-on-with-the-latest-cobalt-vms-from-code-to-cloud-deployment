namespace eShop.WebApp;

public static class TimingLog
{
    private static readonly string? _logPath = OperatingSystem.IsWindows() ? @"C:\es\eShop\timing.log" : null;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Console.WriteLine($"[TIMING] {line}");

        if (_logPath is not null)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); }
            catch { }
        }
    }
}
