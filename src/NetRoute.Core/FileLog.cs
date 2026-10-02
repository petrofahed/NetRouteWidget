using System.Globalization;

namespace NetRoute.Core;

/// Minimal daily-rolling log. Never throws: logging must not crash the widget.
public sealed class FileLog(string directory, TimeProvider? time = null)
{
    public const int KeepDays = 7;

    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly Lock _gate = new();

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}: {exception}");

    public void PruneOldFiles()
    {
        if (!Directory.Exists(directory)) return;
        var oldestKept = _time.GetLocalNow().Date.AddDays(-(KeepDays - 1));
        string[] files;
        try { files = Directory.GetFiles(directory, "netroute-*.log"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var file in files)
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["netroute-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < oldestKept)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    void Write(string level, string message)
    {
        var now = _time.GetLocalNow();
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, $"netroute-{now:yyyyMMdd}.log"),
                    $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
