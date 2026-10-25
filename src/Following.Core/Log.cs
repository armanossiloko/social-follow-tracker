namespace Following.Core;

/// <summary>Minimal logger: appends to the log file and optionally echoes to the console.</summary>
public sealed class Log(string? filePath, bool console)
{
    public static readonly Log Null = new(null, false);

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        if (console)
        {
            var writer = level == "INFO" ? Console.Out : Console.Error;
            writer.WriteLine(level == "INFO" ? message : $"{level}: {message}");
        }
        if (filePath is null) return;
        try
        {
            File.AppendAllText(filePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {level,-5} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never take the job down.
        }
    }
}
