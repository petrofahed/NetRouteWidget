using System.Runtime.InteropServices;
using System.Text;

namespace NetRoute.Core.Windows;

/// The image file name of a running process, from its id.
public static class ProcessImage
{
    const uint ProcessQueryLimitedInformation = 0x1000;

    /// "chrome.exe"; "System" for the kernel's own traffic (id 4); null when the process cannot be opened or has gone.
    public static string? NameOf(int pid)
    {
        if (pid == 4) return "System";
        if (pid <= 0) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            return QueryFullProcessImageNameW(handle, 0, path, ref size) ? Path.GetFileName(path.ToString()) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder name, ref uint size);
}
