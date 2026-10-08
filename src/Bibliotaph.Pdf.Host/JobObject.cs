using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bibliotaph.Pdf.Host;

/// <summary>
/// A Windows job object that caps a worker's committed memory, kills it if the host goes away
/// (KILL_ON_JOB_CLOSE), and allows exactly one process, so a runaway or compromised worker can
/// neither outlive or starve the app nor start anything.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class JobObject : IDisposable
{
    const int ExtendedLimitInformation = 9;
    const uint LimitActiveProcess = 0x00000008;
    const uint LimitProcessMemory = 0x00000100;
    const uint LimitKillOnJobClose = 0x00002000;
    const uint LimitDieOnUnhandledException = 0x00000400;

    readonly IntPtr _handle;

    public JobObject(long memoryLimitBytes)
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = LimitActiveProcess | LimitProcessMemory | LimitKillOnJobClose | LimitDieOnUnhandledException,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = checked((UIntPtr)(ulong)memoryLimitBytes),
        };
        if (!SetInformationJobObject(_handle, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new Win32Exception();
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle)) throw new Win32Exception();
    }

    public long? PeakProcessMemoryUsed
    {
        get
        {
            var size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            return QueryInformationJobObject(_handle, ExtendedLimitInformation, out var info, size, out _)
                ? (long)(ulong)info.PeakProcessMemoryUsed
                : null;
        }
    }

    public void Dispose() => CloseHandle(_handle);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length, out uint returned);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
