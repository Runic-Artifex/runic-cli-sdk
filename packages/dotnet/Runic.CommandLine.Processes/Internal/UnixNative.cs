using System;
using System.Runtime.InteropServices;

namespace Runic.CommandLine.Processes.Internal;

// The libc calls behind UnixGroupProcess. posix_spawn_file_actions_t, posix_spawnattr_t and sigset_t are opaque
// and differ in size between glibc, musl and macOS, so callers pass zeroed buffers larger than any of them.
internal static unsafe partial class UnixNative
{
    private const string LibC = "libc";

    internal const int SpawnObjectBufferSize = 1024;
    internal const int SignalSetBufferSize = 256;

    // Equal on glibc, musl and macOS.
    internal const short PosixSpawnSetProcessGroup = 0x02;
    internal const short PosixSpawnSetSignalDefault = 0x04;
    internal const short PosixSpawnSetSignalMask = 0x08;

    internal const int SignalKill = 9;
    internal const int SignalTerminate = 15;

    internal const int ErrorNotPermitted = 1;
    internal const int ErrorNotFound = 2;
    internal const int ErrorInterrupted = 4;
    internal const int ErrorExecFormat = 8;
    internal const int ErrorAccessDenied = 13;
    internal const int ErrorNotDirectory = 20;

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

    [LibraryImport(LibC, EntryPoint = "sigfillset")]
    internal static partial int SignalSetFill(void* signalSet);

    [LibraryImport(LibC, EntryPoint = "waitpid", SetLastError = true)]
    internal static partial int WaitProcess(int processId, out int status, int options);

    [LibraryImport(LibC, EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int processId, int signal);
}
