using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runic.CommandLine.Processes.Internal;

internal static unsafe partial class WindowsNative
{
    private const string Kernel32 = "kernel32.dll";

    [LibraryImport(Kernel32, EntryPoint = "CreateJobObjectW", SetLastError = true)]
    internal static partial SafeJobHandle CreateJobObject(IntPtr jobAttributes, IntPtr name);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JobObjectLimitKillOnJobClose = 0x2000;

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool SetInformationJobObject(
        SafeJobHandle job,
        int informationClass,
        void* information,
        uint length);

    /// <summary>Sets or clears JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, the job's only limit.</summary>
    internal static unsafe bool SetKillOnJobClose(SafeJobHandle job, bool kill)
    {
        var information = new JobObjectExtendedLimit { LimitFlags = kill ? JobObjectLimitKillOnJobClose : 0 };
        return SetInformationJobObject(job, JobObjectExtendedLimitInformation, &information, (uint)sizeof(JobObjectExtendedLimit));
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: JOBOBJECT_BASIC_LIMIT_INFORMATION, IO_COUNTERS and four SIZE_T values.
    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimit
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    // Cancels a pending synchronous ReadFile on a redirected pipe, which closing the handle does not.
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(true)
    {
    }

    protected override bool ReleaseHandle() => WindowsNative.CloseHandle(handle);
}
