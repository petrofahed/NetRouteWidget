using System.Diagnostics;
using System.Security.Principal;

namespace NetRoute.Core.Windows;

/// Creates and removes the "Start with Windows" scheduled task through schtasks.exe.
/// Enable/Disable need administrator rights; Exists does not.
public sealed class StartupTask(string taskName = "NetRouteWidget")
{
    public bool Exists() => Run("/Query", "/TN", taskName).ExitCode == 0;

    public void Enable(string exePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var xmlPath = Path.Combine(Path.GetTempPath(), $"{taskName}-{Guid.NewGuid():N}.xml");
        try
        {
            StartupTaskXml.Save(StartupTaskXml.Build(exePath, identity.Name), xmlPath);
            Check(Run("/Create", "/TN", taskName, "/XML", xmlPath, "/F"), "create");
        }
        finally
        {
            File.Delete(xmlPath);
        }
    }

    public void Disable()
    {
        if (Exists()) Check(Run("/Delete", "/TN", taskName, "/F"), "delete");
    }

    static void Check((int ExitCode, string Error) result, string action)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not {action} the startup task: {result.Error.Trim()}");
    }

    static (int ExitCode, string Error) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error.Result);
    }
}
