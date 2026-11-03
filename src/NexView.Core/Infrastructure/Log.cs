namespace NexView.Core.Infrastructure;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>Basit, iş parçacığı güvenli dosya günlüğü.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public static string CurrentFile => Path.Combine(AppPaths.LogsDir, $"nexview-{DateTime.Now:yyyyMMdd}.log");

    public static void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinimumLevel) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] {message}";
        if (ex != null) line += Environment.NewLine + ex;
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (Gate) File.AppendAllText(CurrentFile, line + Environment.NewLine);
        }
        catch
        {
            // Günlük yazılamazsa uygulama çalışmaya devam etmeli.
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message, Exception? ex = null) => Write(LogLevel.Warning, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(LogLevel.Error, message, ex);
}
