using System.Diagnostics;

namespace NetRoute.Core.Windows;

/// Runs sing-box.exe hidden, streams its stdout/stderr lines, and reports exits.
/// Exited fires for every exit; when StopAsync caused it, it fires before StopAsync completes.
public sealed class SingBoxHost(string exePath, string configPath) : ISingBoxHost
{
    Process? _process;
    Task? _monitor;

    public bool IsRunning => _process is { HasExited: false };
    public event Action<string>? LineReceived;
    public event Action<int>? Exited;

    public Task StartAsync(string configJson, CancellationToken ct = default)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("sing-box.exe not found next to the app", exePath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, configJson);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) LineReceived?.Invoke(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) LineReceived?.Invoke(line); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _monitor = MonitorAsync(process);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_process is not { } process) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // already gone
        if (_monitor is { } monitor) await monitor.ConfigureAwait(false);
        process.Dispose();
        _process = null;
        _monitor = null;
    }

    async Task MonitorAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.WaitForExit(); // drain redirected output
        Exited?.Invoke(process.ExitCode);
    }

    /// Leftovers from a widget that crashed would keep routing with a stale config; stop them at startup.
    /// Only processes whose main module is exactly exePath are touched.
    public static int KillOrphans(string exePath)
    {
        var killed = 0;
        foreach (var p in Process.GetProcessesByName("sing-box"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return killed;
    }
}
