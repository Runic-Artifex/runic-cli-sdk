using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Runic.CommandLine.Processes.Internal;

// The libc calls behind UnixGroupProcess. posix_spawn_file_actions_t, posix_spawnattr_t and sigset_t are opaque
// and differ in size between glibc, musl and macOS, so callers pass zeroed buffers larger than any of them.
internal static unsafe partial class UnixNative
{
    private const string LibC = "libc";
    private const string ProbeSymbol = "posix_spawn";

    internal const int SpawnObjectBufferSize = 1024;
    internal const int SignalSetBufferSize = 256;

    // Equal on glibc, musl and macOS.
    internal const short PosixSpawnSetProcessGroup = 0x02;
    internal const short PosixSpawnSetSignalDefault = 0x04;
    internal const short PosixSpawnSetSignalMask = 0x08;

    // glibc 2.26 and musl use 0x80; macOS 10.15 uses 0x400.
    internal static short PosixSpawnSetSession => OperatingSystem.IsMacOS() ? (short)0x400 : (short)0x80;

    internal const int IdTypeProcess = 1;
    internal const int WaitExited = 4;

    // Leaves the child waitable: a zombie keeps its PID, and with it the process group ID, reserved.
    internal static int WaitNoWait => OperatingSystem.IsMacOS() ? 0x20 : 0x01000000;

    internal static int SignalChild => OperatingSystem.IsMacOS() ? 20 : 17;

    internal const int SignalPipe = 13;
    internal static readonly IntPtr SignalIgnore = 1;
    internal const int SigactionBufferSize = 256;
    internal const int SiginfoBufferSize = 256;

    internal const int SignalKill = 9;
    internal const int SignalTerminate = 15;

    internal const int ErrorNotPermitted = 1;
    internal const int ErrorNoChild = 10;
    internal const int ErrorNotFound = 2;
    internal const int ErrorInterrupted = 4;
    internal const int ErrorExecFormat = 8;
    internal const int ErrorAccessDenied = 13;
    internal const int ErrorNotDirectory = 20;

    // "libc" is a linker script on glibc and absent by that name on musl. The C library is already loaded in every
    // Unix process, so resolve the symbols from the process itself and fall back to the runtime's probing.
    static UnixNative()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(UnixNative).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibC || OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        IntPtr process = NativeLibrary.GetMainProgramHandle();
        return NativeLibrary.TryGetExport(process, ProbeSymbol, out _) ? process : IntPtr.Zero;
    }

    /// <summary>Reports, once per process, that a child could not get a process group of its own.</summary>
    internal static void ReportUnavailable(Exception exception)
    {
        Trace.TraceWarning(
            "Runic.CommandLine.Processes: posix_spawn is unavailable ({0}); children run in the caller's process " +
            "group and cancellation terminates their process tree instead.",
            exception.GetType().Name);
    }

    [LibraryImport(LibC, EntryPoint = "posix_spawn")]
    internal static partial int PosixSpawn(
        out int processId,
        byte* path,
        void* fileActions,
        void* attributes,
        byte** arguments,
        byte** environment);

    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_init")]
    internal static partial int FileActionsInit(void* fileActions);

    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_destroy")]
    internal static partial int FileActionsDestroy(void* fileActions);

    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_adddup2")]
    internal static partial int FileActionsAddDup2(void* fileActions, int fileDescriptor, int newFileDescriptor);

    // glibc 2.29, musl 1.1.24 and macOS 10.15 export it.
    [LibraryImport(LibC, EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    internal static partial int FileActionsAddChangeDirectory(void* fileActions, byte* path);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_init")]
    internal static partial int AttributesInit(void* attributes);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_destroy")]
    internal static partial int AttributesDestroy(void* attributes);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_setflags")]
    internal static partial int AttributesSetFlags(void* attributes, short flags);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_setpgroup")]
    internal static partial int AttributesSetProcessGroup(void* attributes, int processGroup);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_setsigmask")]
    internal static partial int AttributesSetSignalMask(void* attributes, void* signalSet);

    [LibraryImport(LibC, EntryPoint = "posix_spawnattr_setsigdefault")]
    internal static partial int AttributesSetSignalDefault(void* attributes, void* signalSet);

    [LibraryImport(LibC, EntryPoint = "sigemptyset")]
    internal static partial int SignalSetEmpty(void* signalSet);

    [LibraryImport(LibC, EntryPoint = "sigaddset")]
    internal static partial int SignalSetAdd(void* signalSet, int signal);

    [LibraryImport(LibC, EntryPoint = "sigaction")]
    internal static partial int SignalAction(int signal, void* action, void* previous);

    [LibraryImport(LibC, EntryPoint = "waitid", SetLastError = true)]
    internal static partial int WaitId(int idType, uint id, void* info, int options);

    [LibraryImport(LibC, EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitProcess(int processId, out int status, int options);

    [LibraryImport(LibC, EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int processId, int signal);
}
