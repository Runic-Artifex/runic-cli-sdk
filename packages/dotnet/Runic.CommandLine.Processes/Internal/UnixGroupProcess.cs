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
/// A Unix child started with posix_spawn as the leader of a new process group, so that termination reaches
/// descendants that were reparented after their parent exited.
/// </summary>
internal sealed unsafe class UnixGroupProcess : ChildProcess
{
    private static volatile bool unavailable;

    private readonly AnonymousPipeServerStream? input;
    private readonly AnonymousPipeServerStream output;
    private readonly AnonymousPipeServerStream error;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? exitCode;
    private int disposed;

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

    internal override int? ExitCode => HasExited ? exitCode : null;

    /// <summary>Starts the child in a new process group, or returns false when this platform cannot.</summary>
    /// <exception cref="ProcessStartFailedException">The operating system rejected the start.</exception>
    internal static bool TryStart(ProcessRequest request, [NotNullWhen(true)] out ChildProcess? child)
    {
        child = null;
        // As PID 1, the runtime reaps every child itself, and the exit status would be lost.
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
                processId = Spawn(path, request, input, output, error);
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                unavailable = true;
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
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        input?.Dispose();
        output.Dispose();
        error.Dispose();
    }

    private void SignalGroup(int signal)
    {
        // The group ID stays reserved while any member, or the unreaped leader, exists. Signals are only sent
        // while the request runs, so the ID cannot have been reused by an unrelated group in the meantime.
        if (Volatile.Read(ref disposed) == 0)
        {
            _ = UnixNative.Kill(-ProcessId, signal);
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
        int? code = null;
        while (true)
        {
            int result = UnixNative.WaitProcess(ProcessId, out int status, 0);
            if (result == ProcessId)
            {
                int signal = status & 0x7f;
                // Like Process.ExitCode, a child ended by a signal reports 128 plus the signal number.
                code = signal == 0 ? (status >> 8) & 0xff : 128 + signal;
                break;
            }

            if (result == -1 && Marshal.GetLastPInvokeError() == UnixNative.ErrorInterrupted)
            {
                continue;
            }

            // ECHILD: SIGCHLD is ignored and the kernel reaped the child; the exit code is unknown.
            break;
        }

        exitCode = code;
        exited.TrySetResult();
    }

    private static int Spawn(
        string path,
        ProcessRequest request,
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
            ThrowIfFailed(UnixNative.AttributesSetFlags(
                attributes,
                UnixNative.PosixSpawnSetProcessGroup | UnixNative.PosixSpawnSetSignalDefault |
                UnixNative.PosixSpawnSetSignalMask));
            ThrowIfFailed(UnixNative.AttributesSetProcessGroup(attributes, 0));
            // The runtime blocks and ignores signals (SIGPIPE) that the child must receive normally.
            ThrowIfFailed(UnixNative.SignalSetEmpty(signals));
            ThrowIfFailed(UnixNative.AttributesSetSignalMask(attributes, signals));
            ThrowIfFailed(UnixNative.SignalSetFill(signals));
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
    private static string? ResolveSpawnPath(string fileName)
    {
        if (fileName.Contains('/', StringComparison.Ordinal))
        {
            return Path.GetFullPath(fileName);
        }

        string? processPath = Environment.ProcessPath;
        if (processPath is not null)
        {
            string candidate = Path.Combine(Path.GetDirectoryName(processPath) ?? string.Empty, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        string current = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        return File.Exists(current) ? current : null;
    }

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
