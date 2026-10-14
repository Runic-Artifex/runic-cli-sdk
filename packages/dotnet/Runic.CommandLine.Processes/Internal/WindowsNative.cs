using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runic.CommandLine.Processes.Internal;

internal static partial class WindowsNative
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
