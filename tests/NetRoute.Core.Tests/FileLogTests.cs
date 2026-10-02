using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public sealed class FileLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "netroute-logs-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Writes_timestamped_lines_to_a_daily_file()
    {
        var log = new FileLog(_dir, _time);

        log.Info("Mode set to Phone");
        log.Error("Apply failed", new InvalidOperationException("Access is denied."));

        var lines = File.ReadAllLines(Path.Combine(_dir, "netroute-20261002.log"));
        Assert.Equal("2026-10-02 09:30:00.000 INFO Mode set to Phone", lines[0]);
        Assert.StartsWith("2026-10-02 09:30:00.000 ERROR Apply failed: System.InvalidOperationException: Access is denied.", lines[1]);
    }

    [Fact]
    public void Prune_keeps_seven_days_and_ignores_other_files()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in new[] { "netroute-20261002.log", "netroute-20260926.log", "netroute-20260925.log", "netroute-20260101.log", "notes.txt" })
            File.WriteAllText(Path.Combine(_dir, name), "x");

        new FileLog(_dir, _time).PruneOldFiles();

        var left = Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[] { "netroute-20260926.log", "netroute-20261002.log", "notes.txt" }, left);
    }

    [Fact]
    public void Prune_skips_an_old_file_it_cannot_delete()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "netroute-20260101.log");
        File.WriteAllText(old, "x");
        File.SetAttributes(old, FileAttributes.ReadOnly);
        try
        {
            new FileLog(_dir, _time).PruneOldFiles();

            Assert.True(File.Exists(old));
        }
        finally
        {
            File.SetAttributes(old, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Prune_on_missing_directory_does_nothing()
    {
        new FileLog(_dir, _time).PruneOldFiles();

        Assert.False(Directory.Exists(_dir));
    }
}
