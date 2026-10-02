using System.Diagnostics;

namespace NetRoute.Core.Tests;

/// Runs the break-glass script in dry-run mode only; it must not change anything.
[Trait("Category", "Integration")]
public class RestoreScriptTests
{
    [Fact]
    public void Dry_run_reports_and_changes_nothing()
    {
        var script = Path.Combine(RepoRoot(), "tools", "Restore-Network.cmd");
        Assert.True(File.Exists(script), $"missing {script}");
        var before = MetricSnapshot();

        var (exitCode, output) = Run("cmd.exe", "/c", script, "/check");

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run", output);
        Assert.Contains("settings file:", output);
        Assert.Matches("would restore|nothing to restore", output);
        Assert.Contains("startup task:", output);
        Assert.Equal(before, MetricSnapshot());
    }

    static string MetricSnapshot() =>
        Run("powershell.exe", "-NoProfile", "-Command",
            "Get-NetIPInterface | Sort-Object ifIndex, AddressFamily | ForEach-Object { '{0},{1},{2},{3}' -f $_.ifIndex, $_.AddressFamily, $_.InterfaceMetric, $_.AutomaticMetric }").Output;

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (global.json) not found");
    }

    static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{file} timed out");
        }
        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
