using System.Diagnostics;
using System.Text;

namespace NetRoute.Core.Windows;

/// Runs sing-box.exe hidden, feeds it the config on stdin (no config file ever touches disk), streams its
/// stdout/stderr lines, and reports exits. Exited fires for every exit; when StopAsync caused it, it fires
/// before StopAsync completes. The process is put in a kill-on-close job so it dies with the widget.
public sealed class SingBoxHost(string exePath) : ISingBoxHost
{
    static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    Process? _process;
    Task? _monitor;

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; } // disposed concurrently
        }
    }

    /// True when sing-box is in the widget's kill-on-close job (best effort: it can fail inside a restrictive outer job).
    public bool KillOnParentExit { get; private set; }

    internal Process? CurrentProcess => _process;

    public event Action<string>? LineReceived;
    public event Action<int>? Exited;

    public async Task StartAsync(string configJson, CancellationToken ct = default)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("sing-box.exe not found next to the app", exePath);

        if (_process is { } old)
        {
            if (IsRunning) throw new InvalidOperationException("sing-box is already running");
            await ReleaseAsync(old).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath))!,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("stdin");

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is { } line) Raise(LineReceived, line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) Raise(LineReceived, line); };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        KillOnParentExit = TryAssignToJob(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _monitor = MonitorAsync(process);

        try
        {
            await process.StandardInput.WriteAsync(configJson.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException) { } // exited before reading the config; the monitor reports the exit
    }

    public async Task StopAsync()
    {
        if (_process is not { } process) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // already gone
        await ReleaseAsync(process).ConfigureAwait(false);
    }

    /// Waits (bounded) for the monitor, then forgets and disposes the process. On timeout the process stays
    /// tracked so IsRunning stays truthful.
    async Task ReleaseAsync(Process process)
    {
        if (_monitor is { } monitor)
        {
            try { await monitor.WaitAsync(ExitTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException("sing-box did not exit"); }
        }
        _process = null;
        _monitor = null;
        process.Dispose();
    }

    static bool TryAssignToJob(Process process)
    {
        try
        {
            var job = JobObjectNative.SharedKillOnCloseJob;
            return job != 0 && JobObjectNative.AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    async Task MonitorAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        process.WaitForExit(); // drain redirected output
        Raise(Exited, process.ExitCode);
    }

    /// A misbehaving subscriber must never crash the (elevated) widget or fault the monitor.
    static void Raise<T>(Action<T>? handler, T arg)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList().Cast<Action<T>>())
        {
            try { d(arg); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }

    /// Leftovers from a widget that crashed would keep routing with a stale config; stop them at startup.
    /// Only processes whose main module path is exactly exePath are touched, never by name alone.
    public static int KillOrphans(string exePath)
    {
        var target = Path.GetFullPath(exePath);
        var killed = 0;
        foreach (var p in Process.GetProcessesByName("sing-box"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    killed++;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or AggregateException) { }
            finally { p.Dispose(); }
        }
        return killed;
    }
}
