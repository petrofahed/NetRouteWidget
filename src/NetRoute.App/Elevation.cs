using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace NetRoute.App;

static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// Starts this exe elevated (UAC prompt). False when the user cancels the prompt.
    public static bool TryStartElevated(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
