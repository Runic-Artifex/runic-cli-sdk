using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Runic.CommandLine.Processes.Internal;

/// <summary>A started child with redirected output and the means to stop it and its descendants.</summary>
internal abstract class ChildProcess : IDisposable
{
    internal abstract int ProcessId { get; }

    /// <summary>The process group the child leads, or null when it shares the caller's group.</summary>
    internal abstract int? ProcessGroupId { get; }

    internal abstract Stream? StandardInput { get; }

    internal abstract Stream StandardOutput { get; }

    internal abstract Stream StandardError { get; }

    internal abstract bool HasExited { get; }

    internal abstract int? ExitCode { get; }

    internal abstract Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Asks the child and its descendants to stop: SIGTERM on Unix, immediate termination on Windows.</summary>
    internal abstract void RequestStop();

    /// <summary>Forcibly ends the child and its descendants.</summary>
    internal abstract void Kill();

    /// <summary>Closes one of the child's pipes, ending a read or write that is still pending on it.</summary>
    internal virtual void Close(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    public abstract void Dispose();

    /// <summary>Starts the request's child process.</summary>
    /// <exception cref="ProcessStartFailedException">The operating system rejected the start.</exception>
    internal static ChildProcess Start(ProcessRequest request)
    {
        if (OperatingSystem.IsWindows())
        {
            return ManagedChildProcess.StartProcess(request);
        }

        // With a controlling terminal, a child in a background process group of its own is stopped (SIGTTIN,
        // SIGTTOU) when it prompts on /dev/tty, as ssh, sudo, git and gpg do, and no longer receives the terminal's
        // Ctrl+C or SIGHUP. Such a child stays in the caller's group unless the request detaches it into a new
        // session, where it has no controlling terminal and opening /dev/tty fails.
        bool detach = request.Options.DetachFromTerminal;
        // With SIGCHLD ignored, Process.Start would never see the child exit, so this runner starts it either way.
        if ((detach || !HasControllingTerminal() || UnixGroupProcess.ChildSignalIgnored()) &&
            UnixGroupProcess.TryStart(request, detach, out ChildProcess? child))
        {
            return child;
        }

        return ManagedChildProcess.StartProcess(request);
    }

    private static bool HasControllingTerminal()
    {
        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle terminal = File.OpenHandle(
                "/dev/tty", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // ENXIO: this process has no controlling terminal.
            return false;
        }
    }

    protected static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;
}

internal sealed class ProcessStartFailedException : Exception
{
    internal ProcessStartFailedException(ProcessStartFailureCategory category)
        : base("The process could not be started.")
    {
        Category = category;
    }

    internal ProcessStartFailureCategory Category { get; }
}

/// <summary>
/// A child started through <see cref="Process"/>: always on Windows, where it joins a job object, and on Unix when
/// it inherits a terminal or a process group cannot be created.
/// </summary>
internal sealed class ManagedChildProcess : ChildProcess
{
    private readonly Process process;
    private readonly SafeJobHandle? job;
    private readonly bool redirectsInput;
    private readonly Dictionary<Stream, SafeFileHandle>? pipeHandles;

    private ManagedChildProcess(Process process, SafeJobHandle? job, bool redirectsInput)
    {
        this.process = process;
        this.job = job;
        this.redirectsInput = redirectsInput;
        ProcessId = process.Id;
        if (OperatingSystem.IsWindows())
        {
            // Read the handles before any I/O: FileStream.SafeFileHandle flushes, which waits for pending operations.
            pipeHandles = new Dictionary<Stream, SafeFileHandle>(ReferenceEqualityComparer.Instance);
            foreach (Stream? stream in new[] { StandardInput, StandardOutput, StandardError })
            {
                if (stream is FileStream file)
                {
                    pipeHandles[stream] = file.SafeFileHandle;
                }
            }
        }
    }

    internal override int ProcessId { get; }

    internal override int? ProcessGroupId => null;

    internal override Stream? StandardInput => redirectsInput ? process.StandardInput.BaseStream : null;

    internal override Stream StandardOutput => process.StandardOutput.BaseStream;

    internal override Stream StandardError => process.StandardError.BaseStream;

    internal override bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return false;
            }
        }
    }

    internal override int? ExitCode
    {
        get
        {
            try
            {
                return process.ExitCode;
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                return null;
            }
        }
    }

    internal static ManagedChildProcess StartProcess(ProcessRequest request)
    {
        var process = new Process { StartInfo = CreateStartInfo(request) };
        try
        {
            if (!process.Start())
            {
                throw new ProcessStartFailedException(ProcessStartFailureCategory.Other);
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        // The child may start descendants before it joins the job; RequestStop and Kill also end the process tree.
        SafeJobHandle? job = OperatingSystem.IsWindows() ? TryAssignJob(process) : null;
        return new ManagedChildProcess(
            process,
            job,
            request.Options.StandardInput.Mode != ProcessStandardInputMode.Inherit);
    }

    internal override Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    internal override void RequestStop()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows has no termination request that a console child without a console of its own receives.
            Kill();
            return;
        }

        if (!HasExited)
        {
            _ = UnixNative.Kill(ProcessId, UnixNative.SignalTerminate);
        }
    }

    internal override void Kill()
    {
        if (job is not null)
        {
            _ = WindowsNative.TerminateJobObject(job, 1);
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or NotSupportedException)
        {
            try
            {
                process.Kill();
            }
            catch (Exception inner) when (!IsFatal(inner))
            {
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    internal override void Close(Stream stream)
    {
        // The pipes are synchronous handles. Closing one does not end a ReadFile or WriteFile that another thread
        // is blocked in, for example while a descendant holds the other end, so cancel it first.
        if (pipeHandles is not null && pipeHandles.TryGetValue(stream, out SafeFileHandle? handle))
        {
            try
            {
                _ = WindowsNative.CancelIoEx(handle, IntPtr.Zero);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
            }
        }

        base.Close(stream);
    }

    public override void Dispose()
    {
        job?.Dispose();
        process.Dispose();
    }

    private static SafeJobHandle? TryAssignJob(Process process)
    {
        SafeJobHandle? job = null;
        try
        {
            job = WindowsNative.CreateJobObject(IntPtr.Zero, IntPtr.Zero);
            if (!job.IsInvalid && WindowsNative.AssignProcessToJobObject(job, process.SafeHandle))
            {
                return job;
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }

        job?.Dispose();
        return null;
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.Options.StandardInput.Mode != ProcessStandardInputMode.Inherit,
            CreateNoWindow = true,
            StandardOutputEncoding = request.Options.StandardOutputEncoding,
            StandardErrorEncoding = request.Options.StandardErrorEncoding,
        };

        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        for (int index = 0; index < request.Arguments.Count; index++)
        {
            startInfo.ArgumentList.Add(request.Arguments[index]);
        }

        if (!request.Options.InheritEnvironment)
        {
            startInfo.Environment.Clear();
        }

        foreach (System.Collections.Generic.KeyValuePair<string, string?> pair in request.Environment)
        {
            if (pair.Value is null)
            {
                startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return startInfo;
    }
}
