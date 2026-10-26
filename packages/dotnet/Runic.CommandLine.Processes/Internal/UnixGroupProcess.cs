using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Processes.Internal;

/// <summary>
/// A Unix child started with posix_spawn as the leader of a new process group, or of a new session without a
/// controlling terminal, so that termination reaches descendants that were reparented after their parent exited.
/// </summary>
internal sealed unsafe class UnixGroupProcess : ChildProcess
{
    private static volatile bool unavailable;

    private readonly AnonymousPipeServerStream? input;
    private readonly AnonymousPipeServerStream output;
    private readonly AnonymousPipeServerStream error;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private int? exitCode;
    private bool statusLost;
    private bool reaped;
    private bool disposed;

    private UnixGroupProcess(
        int processId,
        AnonymousPipeServerStream? input,
        AnonymousPipeServerStream output,
        AnonymousPipeServerStream error)
    {
        ProcessId = processId;
        this.input = input;
        this.output = output;
        this.error = error;
    }

    internal override int ProcessId { get; }

    internal override int? ProcessGroupId => ProcessId;

    internal override Stream? StandardInput => input;

    internal override Stream StandardOutput => output;

    internal override Stream StandardError => error;

    internal override bool HasExited => exited.Task.IsCompleted;

    /// <summary>Gets the exit code, reaping the child; null while it runs or when another party reaped it.</summary>
    internal override int? ExitCode
    {
        get
        {
            if (!HasExited)
            {
                return null;
            }

            lock (gate)
            {
                Reap();
                return exitCode;
            }
        }
    }

    /// <summary>
    /// Starts the child as the leader of a new process group, or with <paramref name="newSession"/> of a new session
    /// without a controlling terminal, or returns false when this platform cannot.
    /// </summary>
    /// <exception cref="ProcessStartFailedException">The operating system rejected the start.</exception>
    internal static bool TryStart(ProcessRequest request, bool newSession, [NotNullWhen(true)] out ChildProcess? child)
    {
        child = null;
        // As PID 1, the runtime reaps every child itself, and the exit status would be lost; Process.Start
        // handles that.
        if (unavailable || Environment.ProcessId == 1)
        {
            return false;
        }

        string path = ResolveSpawnPath(request.FileName)
            ?? throw new ProcessStartFailedException(ProcessStartFailureCategory.NotFound);

        AnonymousPipeServerStream? input = null;
        AnonymousPipeServerStream? output = null;
        AnonymousPipeServerStream? error = null;
        try
        {
            // Both ends are close-on-exec; dup2 gives the child its standard descriptors without that flag, so
            // no other process started meanwhile inherits these pipes.
            if (request.Options.StandardInput.Mode != ProcessStandardInputMode.Inherit)
            {
                input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
            }

            output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
            error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

            int processId;
            try
            {
                processId = Spawn(path, request, newSession, input, output, error);
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                unavailable = true;
                UnixNative.ReportUnavailable(exception);
                input?.Dispose();
                output.Dispose();
                error.Dispose();
                return false;
            }

            input?.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();

            var started = new UnixGroupProcess(processId, input, output, error);
            started.StartExitWaiter();
            child = started;
            return true;
        }
        catch
        {
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
            throw;
        }
    }

    internal override Task WaitForExitAsync(CancellationToken cancellationToken) =>
        exited.Task.WaitAsync(cancellationToken);

    internal override void RequestStop() => SignalGroup(UnixNative.SignalTerminate);

    internal override void Kill() => SignalGroup(UnixNative.SignalKill);

    public override void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            // A leader that is still running is reaped by its waiter thread when it exits.
            if (HasExited)
            {
                Reap();
            }
        }

        input?.Dispose();
        output.Dispose();
        error.Dispose();
    }

    private void SignalGroup(int signal)
    {
        // The waiter does not reap the leader, so until Reap its PID, and with it the group ID, cannot be reused:
        // a signal sent before then reaches only this group, also when the leader has already exited.
        lock (gate)
        {
            if (!reaped && !disposed)
            {
                _ = UnixNative.Kill(-ProcessId, signal);
            }
        }
    }

    // Called under the gate, after exit.
    private void Reap()
    {
        if (reaped)
        {
            return;
        }

        reaped = true;
        if (statusLost)
        {
            return;
        }

        while (true)
        {
            int result = UnixNative.WaitProcess(ProcessId, out int status, 0);
            if (result == ProcessId)
            {
                int signal = status & 0x7f;
                // Like Process.ExitCode, a child ended by a signal reports 128 plus the signal number.
                exitCode = signal == 0 ? (status >> 8) & 0xff : 128 + signal;
                return;
            }

            if (result == -1 && Marshal.GetLastPInvokeError() == UnixNative.ErrorInterrupted)
            {
                continue;
            }

            return;
        }
    }

    /// <summary>
    /// Gets a value indicating whether this process ignores SIGCHLD, so that the kernel reaps children itself.
    /// Process.Start then never observes an exit; this runner observes it and reports the status as lost.
    /// </summary>
    internal static bool ChildSignalIgnored()
    {
        if (unavailable)
        {
            return false;
        }

        try
        {
            byte* previous = stackalloc byte[UnixNative.SigactionBufferSize];
            new Span<byte>(previous, UnixNative.SigactionBufferSize).Clear();
            // The handler is the first member of struct sigaction on Linux and macOS.
            return UnixNative.SignalAction(UnixNative.SignalChild, null, previous) == 0 &&
                *(IntPtr*)previous == UnixNative.SignalIgnore;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    private void StartExitWaiter()
    {
        var thread = new Thread(static state => ((UnixGroupProcess)state!).WaitForExit(), 128 * 1024)
        {
            IsBackground = true,
            Name = "Runic process exit",
        };
        thread.Start(this);
    }

    private void WaitForExit()
    {
        byte* info = stackalloc byte[UnixNative.SiginfoBufferSize];
        bool lost = false;
        while (true)
        {
            int result = UnixNative.WaitId(
                UnixNative.IdTypeProcess,
                (uint)ProcessId,
                info,
                UnixNative.WaitExited | UnixNative.WaitNoWait);
            if (result == 0)
            {
                break;
            }

            if (Marshal.GetLastPInvokeError() == UnixNative.ErrorInterrupted)
            {
                continue;
            }

            // ECHILD: another party reaped the child, for example the kernel because SIGCHLD was ignored after the
            // start. The exit code is unknown, and the runner reports the request as not observed.
            lost = true;
            break;
        }

        lock (gate)
        {
            statusLost = lost;
            // Already reaped by someone else: the PID may be reused, so never signal the group again.
            reaped |= lost;
            exited.TrySetResult();
            if (disposed)
            {
                Reap();
            }
        }
    }

    private static int Spawn(
        string path,
        ProcessRequest request,
        bool newSession,
        AnonymousPipeServerStream? input,
        AnonymousPipeServerStream output,
        AnonymousPipeServerStream error)
    {
        var allocations = new List<IntPtr>();
        void* fileActions = NativeMemory.AllocZeroed(UnixNative.SpawnObjectBufferSize);
        void* attributes = NativeMemory.AllocZeroed(UnixNative.SpawnObjectBufferSize);
        void* signals = NativeMemory.AllocZeroed(UnixNative.SignalSetBufferSize);
        bool fileActionsReady = false;
        bool attributesReady = false;
        try
        {
            byte* nativePath = Utf8(path, allocations);
            var arguments = new List<string>(request.Arguments.Count + 1) { path };
            arguments.AddRange(request.Arguments);
            byte** argumentVector = Vector(arguments, allocations);
            byte** environmentVector = Vector(CreateEnvironment(request), allocations);

            ThrowIfFailed(UnixNative.FileActionsInit(fileActions));
            fileActionsReady = true;
            if (input is not null)
            {
                ThrowIfFailed(UnixNative.FileActionsAddDup2(fileActions, Descriptor(input), 0));
            }

            ThrowIfFailed(UnixNative.FileActionsAddDup2(fileActions, Descriptor(output), 1));
            ThrowIfFailed(UnixNative.FileActionsAddDup2(fileActions, Descriptor(error), 2));
            if (request.WorkingDirectory is not null)
            {
                ThrowIfFailed(UnixNative.FileActionsAddChangeDirectory(
                    fileActions,
                    Utf8(request.WorkingDirectory, allocations)));
            }

            ThrowIfFailed(UnixNative.AttributesInit(attributes));
            attributesReady = true;
            // A new session also leads a new process group; setpgid would fail for a session leader.
            ThrowIfFailed(UnixNative.AttributesSetFlags(
                attributes,
                (short)((newSession ? UnixNative.PosixSpawnSetSession : UnixNative.PosixSpawnSetProcessGroup) |
                    UnixNative.PosixSpawnSetSignalDefault | UnixNative.PosixSpawnSetSignalMask)));
            if (!newSession)
            {
                ThrowIfFailed(UnixNative.AttributesSetProcessGroup(attributes, 0));
            }

            // exec resets caught signals, but not ignored ones. Like Process.Start, restore only SIGPIPE, which the
            // runtime ignores; a disposition the caller inherited as ignored, such as SIGHUP under nohup, is kept.
            ThrowIfFailed(UnixNative.SignalSetEmpty(signals));
            ThrowIfFailed(UnixNative.AttributesSetSignalMask(attributes, signals));
            ThrowIfFailed(UnixNative.SignalSetAdd(signals, UnixNative.SignalPipe));
            ThrowIfFailed(UnixNative.AttributesSetSignalDefault(attributes, signals));

            int result = UnixNative.PosixSpawn(
                out int processId,
                nativePath,
                fileActions,
                attributes,
                argumentVector,
                environmentVector);
            ThrowIfFailed(result);
            GC.KeepAlive(input);
            GC.KeepAlive(output);
            GC.KeepAlive(error);
            return processId;
        }
        finally
        {
            if (fileActionsReady)
            {
                _ = UnixNative.FileActionsDestroy(fileActions);
            }

            if (attributesReady)
            {
                _ = UnixNative.AttributesDestroy(attributes);
            }

            NativeMemory.Free(fileActions);
            NativeMemory.Free(attributes);
            NativeMemory.Free(signals);
            foreach (IntPtr allocation in allocations)
            {
                NativeMemory.Free((void*)allocation);
            }
        }
    }

    private static int Descriptor(AnonymousPipeServerStream pipe) =>
        checked((int)pipe.ClientSafePipeHandle.DangerousGetHandle());

    private static void ThrowIfFailed(int result)
    {
        if (result == 0)
        {
            return;
        }

        throw new ProcessStartFailedException(result switch
        {
            UnixNative.ErrorNotFound or UnixNative.ErrorNotDirectory => ProcessStartFailureCategory.NotFound,
            UnixNative.ErrorAccessDenied or UnixNative.ErrorNotPermitted => ProcessStartFailureCategory.AccessDenied,
            UnixNative.ErrorExecFormat => ProcessStartFailureCategory.Unsupported,
            _ => ProcessStartFailureCategory.Other,
        });
    }

    // The runner already searched the effective PATH. Process.Start then tries the application directory and
    // the current directory, so an unresolved bare name keeps that behavior here.
    // ProcessRunner passes a resolved path. A bare name is never looked up in the application or current directory
    // (SEC-38).
    private static string? ResolveSpawnPath(string fileName) =>
        fileName.Contains('/', StringComparison.Ordinal) ? Path.GetFullPath(fileName) : null;

    private static List<string> CreateEnvironment(ProcessRequest request)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Options.InheritEnvironment)
        {
            // The managed view: Environment.SetEnvironmentVariable does not change the native environ on Unix.
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                variables[(string)entry.Key] = entry.Value as string ?? string.Empty;
            }
        }

        foreach (KeyValuePair<string, string?> pair in request.Environment)
        {
            if (pair.Value is null)
            {
                variables.Remove(pair.Key);
            }
            else
            {
                variables[pair.Key] = pair.Value;
            }
        }

        var entries = new List<string>(variables.Count);
        foreach (KeyValuePair<string, string> pair in variables)
        {
            entries.Add(string.Concat(pair.Key, "=", pair.Value));
        }

        return entries;
    }

    private static byte** Vector(List<string> values, List<IntPtr> allocations)
    {
        var vector = (byte**)NativeMemory.AllocZeroed((nuint)(values.Count + 1), (nuint)sizeof(byte*));
        allocations.Add((IntPtr)vector);
        for (int index = 0; index < values.Count; index++)
        {
            vector[index] = Utf8(values[index], allocations);
        }

        return vector;
    }

    private static byte* Utf8(string value, List<IntPtr> allocations)
    {
        int length = System.Text.Encoding.UTF8.GetByteCount(value);
        var buffer = (byte*)NativeMemory.Alloc((nuint)length + 1);
        allocations.Add((IntPtr)buffer);
        System.Text.Encoding.UTF8.GetBytes(value, new Span<byte>(buffer, length));
        buffer[length] = 0;
        return buffer;
    }
}
