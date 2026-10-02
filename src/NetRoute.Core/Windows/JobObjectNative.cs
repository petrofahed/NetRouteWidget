using System.Runtime.InteropServices;

namespace NetRoute.Core.Windows;

/// kernel32 job-object interop. A job with KILL_ON_JOB_CLOSE kills its processes when the last handle
/// to it closes, which Windows does for us if the owning (widget) process dies for any reason.
/// Struct layouts follow winnt.h (JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on x64).
internal static class JobObjectNative
{
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetInformationJobObject(nint job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    static readonly Lock Gate = new();
    static bool _created;
    static nint _job;

    /// One job for the whole widget process, created lazily and deliberately never closed:
    /// it closes when the process dies, which is exactly when sing-box must die with it. Zero if unavailable.
    public static nint SharedKillOnCloseJob
    {
        get
        {
            lock (Gate)
            {
                if (_created) return _job;
                _created = true;
                var job = CreateJobObjectW(0, null);
                if (job == 0) return 0;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    CloseHandle(job);
                    return 0;
                }
                _job = job;
                return _job;
            }
        }
    }
}
