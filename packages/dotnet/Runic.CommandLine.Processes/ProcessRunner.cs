using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine.Processes.Internal;

namespace Runic.CommandLine.Processes;

/// <summary>Provides shell-free, bounded, cancellable local process execution.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int BufferSize = 81920;
    private static readonly ProcessOutput EmptyOutput = ProcessResult.Empty;
    private static readonly SearchValues<char> BatchArgumentMetacharacters =
        SearchValues.Create("%!^&|<>\"()\r\n");
    private static readonly SearchValues<char> BatchPathMetacharacters = SearchValues.Create("%!\"\r\n");
    private readonly IExecutablePolicy executablePolicy;
    private readonly IProcessObserver? observer;
    private readonly TimeProvider timeProvider;
    private readonly ProcessPlatform platform;

    /// <summary>Initializes a process runner.</summary>
    /// <param name="executablePolicy">Policy evaluated before every start.</param>
    /// <param name="observer">Optional metadata-only observer.</param>
    /// <param name="timeProvider">Time source used for result timestamps, timeouts and drain grace periods.</param>
    public ProcessRunner(
        IExecutablePolicy executablePolicy,
        IProcessObserver? observer = null,
        TimeProvider? timeProvider = null)
        : this(executablePolicy, observer, timeProvider, ProcessPlatform.Current)
    {
    }

    internal ProcessRunner(
        IExecutablePolicy executablePolicy,
        IProcessObserver? observer,
        TimeProvider? timeProvider,
        ProcessPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(executablePolicy);
        ArgumentNullException.ThrowIfNull(platform);
        this.executablePolicy = executablePolicy;
        this.observer = observer;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.platform = platform;
    }


    /// <summary>Runs one bounded request, passing its output to a handler while it runs when one is given.</summary>
    /// <param name="request">The immutable process request.</param>
    /// <param name="outputHandler">Receives the output while the child runs; null runs the request without one.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>A sanitized terminal result, with the bounded capture of both streams.</returns>
    /// <remarks>
    /// If the handler throws while the request is neither cancelled nor timed out, the runner terminates the child
    /// as on cancellation and returns <see cref="ProcessState.ExecutionFailed"/> with
    /// <see cref="ProcessFaultCodes.OutputHandlerFailed"/>. After the result is returned, the handler is not called
    /// again; a call that ignores its cancellation token may still be running.
    /// </remarks>
    public async ValueTask<ProcessResult> RunAsync(
        ProcessRequest request,
        IProcessOutputHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset startedAt = timeProvider.GetUtcNow();

        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(
                ProcessState.Cancelled,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                null,
                startedAt);
        }

        ProcessRequest effectiveRequest;
        try
        {
            effectiveRequest = ResolveRequest(request);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return StartFailed(ProcessStartFailureCategory.InvalidRequest, startedAt);
        }

        CommandFault? batchFault = ValidateBatchFile(effectiveRequest);
        if (batchFault is not null)
        {
            return Complete(
                ProcessState.Rejected,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                batchFault,
                startedAt);
        }

        ExecutablePolicyDecision decision;
        try
        {
            decision = executablePolicy.Evaluate(effectiveRequest);
            if (decision is null || (!decision.IsAllowed && decision.Fault is null))
            {
                return Complete(
                    ProcessState.Rejected,
                    null,
                    EmptyOutput,
                    EmptyOutput,
                    null,
                    new CommandFault(
                        ProcessFaultCodes.InvalidPolicyDecision,
                        "The executable policy returned an invalid decision."),
                    startedAt);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Complete(
                ProcessState.Rejected,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                new CommandFault(
                    ProcessFaultCodes.PolicyEvaluationFailed,
                    "The executable policy could not evaluate the request."),
                startedAt);
        }

        if (!decision.IsAllowed)
        {
            return Complete(
                ProcessState.Rejected,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                NormalizePolicyFault(decision.Fault!),
                startedAt);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Complete(
                ProcessState.Cancelled,
                null,
                EmptyOutput,
                EmptyOutput,
                null,
                null,
                startedAt);
        }

        ChildProcess child;
        try
        {
            child = ChildProcess.Start(effectiveRequest);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return StartFailed(ClassifyStartFailure(exception), startedAt);
        }

        using (child)
        {
            return await RunStartedAsync(child, effectiveRequest, outputHandler, startedAt, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<ProcessResult> RunStartedAsync(
        ChildProcess child,
        ProcessRequest request,
        IProcessOutputHandler? outputHandler,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        NotifyStarted(new ProcessIdentity(child.ProcessId, child.ProcessGroupId), startedAt);
        ProcessExecutionOptions options = request.Options;

        using CancellationTokenSource? timeout = CreateTimeout(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout?.Token ?? CancellationToken.None);
        OutputDelivery? delivery = outputHandler is null ? null : new OutputDelivery(outputHandler, linked.Token);
        var standardOutput = new OutputCapture(options.StandardOutputLimitBytes, options.StandardOutputEncoding);
        var standardError = new OutputCapture(options.StandardErrorLimitBytes, options.StandardErrorEncoding);
        Task standardOutputPump = OutputPump.PumpAsync(child.StandardOutput, standardOutput,
            ProcessOutputChannel.StandardOutput, delivery, options.StandardOutputEncoding);
        Task standardErrorPump = OutputPump.PumpAsync(child.StandardError, standardError,
            ProcessOutputChannel.StandardError, delivery, options.StandardErrorEncoding);

        using var inputStop = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        Stream? inputStream = child.StandardInput;
        Task input = inputStream is null
            ? Task.CompletedTask
            : WriteInputAsync(inputStream, options.StandardInput.Bytes, inputStop.Token);
        var pipes = new Pipes(child, standardOutputPump, standardErrorPump, input);

        ProcessState state = ProcessState.Exited;
        CommandFault? fault = null;
        try
        {
            Task exit = child.WaitForExitAsync(linked.Token);
            if (delivery is not null &&
                await Task.WhenAny(exit, delivery.Failure).ConfigureAwait(false) != exit &&
                !child.HasExited)
            {
                state = ProcessState.ExecutionFailed;
                fault = new CommandFault(ProcessFaultCodes.OutputHandlerFailed, "The output handler failed.");
            }
            else
            {
                await exit.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            if (!child.HasExited)
            {
                state = cancellationToken.IsCancellationRequested
                    ? ProcessState.Cancelled
                    : ProcessState.TimedOut;
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            state = ProcessState.ExecutionFailed;
            fault = new CommandFault(ProcessFaultCodes.ExecutionFailed, ProcessResult.ExecutionFailedMessage);
        }

        // A child may exit without reading all input, or a descendant may inherit the read end.
        // Stop input independently of output draining and never wait for it before observing exit.
        inputStop.Cancel();
        if (inputStream is not null)
        {
            child.Close(inputStream);
        }

        DrainOutcome drain = state == ProcessState.Exited
            ? await AwaitDrainAsync(pipes, options.DrainGracePeriod, delivery, linked.Token).ConfigureAwait(false)
            : await TerminateAndDrainAsync(pipes, options.DrainGracePeriod, delivery).ConfigureAwait(false);
        delivery?.Stop();

        // A short child can exit before its handler fails on output that is still in the pipe.
        if (state == ProcessState.Exited && delivery?.Failure.IsCompleted == true)
        {
            state = ProcessState.ExecutionFailed;
            fault = new CommandFault(ProcessFaultCodes.OutputHandlerFailed, "The output handler failed.");
        }

        int? exitCode = state == ProcessState.Exited ? child.ExitCode : null;
        if (state == ProcessState.Exited && exitCode is null)
        {
            // Another party reaped the child, for example because SIGCHLD was ignored, so its outcome is unknown.
            state = ProcessState.ExecutionFailed;
            fault = new CommandFault(ProcessFaultCodes.ExecutionFailed, ProcessResult.ExecutionFailedMessage);
        }

        return Complete(
            state,
            exitCode,
            standardOutput.ToOutput(drain.StandardOutputTimedOut),
            standardError.ToOutput(drain.StandardErrorTimedOut),
            null,
            fault,
            startedAt);
    }

    private ProcessRequest ResolveRequest(ProcessRequest request)
    {
        string resolvedFileName = ResolveExecutable(request);
        if (string.Equals(resolvedFileName, request.FileName, StringComparison.Ordinal))
        {
            return request;
        }

        return new ProcessRequest(
            resolvedFileName,
            request.Arguments,
            request.WorkingDirectory,
            request.Environment,
            request.Options);
    }

    private string ResolveExecutable(ProcessRequest request)
    {
        if (Path.IsPathFullyQualified(request.FileName))
        {
            return Path.GetFullPath(request.FileName);
        }

        if (request.FileName.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            request.FileName.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return Path.GetFullPath(request.FileName);
        }

        string? pathValue = GetEffectiveEnvironmentValue(request, "PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return UnresolvedExecutable(request);
        }

        string[] extensions = GetExecutableExtensions(request.FileName, request);
        string[] directories = pathValue.Split(
            platform.PathListSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
        {
            for (int extensionIndex = 0; extensionIndex < extensions.Length; extensionIndex++)
            {
                string candidate = Path.GetFullPath(
                    Path.Combine(directories[directoryIndex], request.FileName + extensions[extensionIndex]));
                if (platform.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return UnresolvedExecutable(request);
    }

    private static string UnresolvedExecutable(ProcessRequest request) =>
        // Process.Start can otherwise search the parent PATH even after Environment.Clear().
        request.Options.InheritEnvironment ? request.FileName : Path.GetFullPath(request.FileName);

    private string[] GetExecutableExtensions(string fileName, ProcessRequest request)
    {
        if (!platform.IsWindows || Path.HasExtension(fileName))
        {
            return [string.Empty];
        }

        string? pathExtensions = GetEffectiveEnvironmentValue(request, "PATHEXT");
        return string.IsNullOrWhiteSpace(pathExtensions)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : pathExtensions.Split(
                platform.PathListSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private string? GetEffectiveEnvironmentValue(ProcessRequest request, string name)
    {
        StringComparison comparison = platform.IsWindows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (System.Collections.Generic.KeyValuePair<string, string?> pair in request.Environment)
        {
            if (string.Equals(pair.Key, name, comparison))
            {
                return pair.Value;
            }
        }

        return request.Options.InheritEnvironment ? platform.GetEnvironmentVariable(name) : null;
    }

    // Windows starts .bat and .cmd files through cmd.exe, which re-parses the command line and
    // interprets metacharacters even inside the quotes ArgumentList adds around a token.
    private CommandFault? ValidateBatchFile(ProcessRequest request)
    {
        if (!platform.IsWindows || !IsBatchFile(request.FileName))
        {
            return null;
        }

        if (!request.Options.AllowWindowsBatchFiles)
        {
            return new CommandFault(
                ProcessFaultCodes.BatchFileRejected,
                "Windows batch files run through cmd.exe and require AllowWindowsBatchFiles.");
        }

        if (request.FileName.AsSpan().ContainsAny(BatchPathMetacharacters))
        {
            return new CommandFault(
                ProcessFaultCodes.BatchFileArgumentRejected,
                "The batch file path contains characters that cmd.exe would interpret.");
        }

        for (int index = 0; index < request.Arguments.Count; index++)
        {
            if (request.Arguments[index].AsSpan().ContainsAny(BatchArgumentMetacharacters))
            {
                return new CommandFault(
                    ProcessFaultCodes.BatchFileArgumentRejected,
                    "A batch file argument contains characters that cmd.exe would interpret.",
                    new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["argumentIndex"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }
        }

        return null;
    }

    private static bool IsBatchFile(string fileName)
    {
        // Windows ignores trailing dots and spaces, so "tool.cmd." starts tool.cmd.
        string extension = Path.GetExtension(fileName.TrimEnd('.', ' '));
        return string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WriteInputAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!bytes.IsEmpty)
            {
                int count = Math.Min(bytes.Length, BufferSize);
                await stream.WriteAsync(bytes[..count], cancellationToken).ConfigureAwait(false);
                bytes = bytes[count..];
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // Early exit, a child closing stdin, and cancellation can all break the write pipe.
            // The child's observed lifecycle and exit code remain authoritative.
        }
        finally
        {
            TryClose(stream);
        }
    }

    // Cancellation, timeout or failure: ask the process group to stop (SIGTERM), give it the drain grace period to
    // exit and close its pipes, then kill whatever is left of the group (SIGKILL) and wait once more.
    private async Task<DrainOutcome> TerminateAndDrainAsync(Pipes pipes, TimeSpan gracePeriod, OutputDelivery? delivery)
    {
        pipes.Child.RequestStop();
        Task all = Task.WhenAll(
            IgnoreFailureAsync(pipes.Child.WaitForExitAsync(CancellationToken.None)),
            pipes.All);
        bool completed = await WaitAsync(all, gracePeriod).ConfigureAwait(false);
        // Also when the leader exited: a descendant that ignored the request must not outlive the request.
        pipes.Child.Kill();
        if (completed || await WaitAsync(all, gracePeriod).ConfigureAwait(false))
        {
            return default;
        }

        return await ClosePipesAsync(pipes, delivery).ConfigureAwait(false);
    }

    // Normal exit: a descendant that inherited a pipe can hold it open after the child exits, so wait at most the
    // grace period for the pipes to close. While an output handler makes progress or a handler call is still
    // running, the wait goes on until the handler is idle for the grace period, or the request is cancelled or
    // times out.
    private async Task<DrainOutcome> AwaitDrainAsync(
        Pipes pipes,
        TimeSpan gracePeriod,
        OutputDelivery? delivery,
        CancellationToken cancellationToken)
    {
        long progress = delivery?.Progress ?? 0;
        while (!await WaitAsync(pipes.All, gracePeriod).ConfigureAwait(false))
        {
            if (delivery is null || cancellationToken.IsCancellationRequested ||
                (delivery.Progress == progress && !delivery.IsBusy))
            {
                return await ClosePipesAsync(pipes, delivery).ConfigureAwait(false);
            }

            progress = delivery.Progress;
        }

        return default;
    }

    private async Task<bool> WaitAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout, timeProvider).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // A pipe still open blames a descendant that holds it, unless a handler call is still running: then the
    // handler, not a descendant, kept the pipe from draining.
    private async Task<DrainOutcome> ClosePipesAsync(Pipes pipes, OutputDelivery? delivery)
    {
        bool handlerBusy = delivery?.IsBusy == true;
        var outcome = new DrainOutcome(
            !pipes.StandardOutput.IsCompleted && !handlerBusy,
            !pipes.StandardError.IsCompleted && !handlerBusy);
        pipes.Child.Close(pipes.Child.StandardOutput);
        pipes.Child.Close(pipes.Child.StandardError);
        await Task.WhenAny(pipes.All, Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider)).ConfigureAwait(false);
        return outcome;
    }

    private static void TryClose(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private CancellationTokenSource? CreateTimeout(TimeSpan timeout) =>
        timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout, timeProvider);

    private ProcessResult StartFailed(ProcessStartFailureCategory category, DateTimeOffset startedAt) =>
        Complete(
            ProcessState.StartFailed,
            null,
            EmptyOutput,
            EmptyOutput,
            category,
            ProcessResult.CreateStartFault(category),
            startedAt);

    private ProcessResult Complete(
        ProcessState state,
        int? exitCode,
        ProcessOutput standardOutput,
        ProcessOutput standardError,
        ProcessStartFailureCategory? failureCategory,
        CommandFault? fault,
        DateTimeOffset startedAt)
    {
        DateTimeOffset endedAt = timeProvider.GetUtcNow();
        NotifyCompleted(state, endedAt - startedAt);
        return new ProcessResult(
            state,
            exitCode,
            standardOutput,
            standardError,
            failureCategory,
            fault,
            startedAt,
            endedAt);
    }

    private static ProcessStartFailureCategory ClassifyStartFailure(Exception exception) => exception switch
    {
        ProcessStartFailedException startFailure => startFailure.Category,
        System.ComponentModel.Win32Exception windowsException when windowsException.NativeErrorCode is 2 or 3 =>
            ProcessStartFailureCategory.NotFound,
        System.ComponentModel.Win32Exception windowsException when windowsException.NativeErrorCode is 5 =>
            ProcessStartFailureCategory.AccessDenied,
        FileNotFoundException or DirectoryNotFoundException => ProcessStartFailureCategory.NotFound,
        UnauthorizedAccessException => ProcessStartFailureCategory.AccessDenied,
        ArgumentException or InvalidOperationException => ProcessStartFailureCategory.InvalidRequest,
        PlatformNotSupportedException or NotSupportedException => ProcessStartFailureCategory.Unsupported,
        _ => ProcessStartFailureCategory.Other,
    };

    private static CommandFault NormalizePolicyFault(CommandFault fault)
    {
        string code = fault.Code == ProcessFaultCodes.WorkingDirectoryRejected
            ? ProcessFaultCodes.WorkingDirectoryRejected
            : ProcessFaultCodes.ExecutableRejected;
        return new CommandFault(code, ProcessResult.PolicyRejectionMessage);
    }


    private void NotifyStarted(ProcessIdentity process, DateTimeOffset startedAt)
    {
        try
        {
            observer?.OnStarted(process, startedAt);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private void NotifyCompleted(ProcessState state, TimeSpan duration)
    {
        try
        {
            observer?.OnCompleted(state, duration);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private readonly record struct DrainOutcome(bool StandardOutputTimedOut, bool StandardErrorTimedOut);

    private sealed class Pipes
    {
        internal Pipes(ChildProcess child, Task standardOutput, Task standardError, Task input)
        {
            Child = child;
            StandardOutput = standardOutput;
            StandardError = standardError;
            All = Task.WhenAll(standardOutput, standardError, IgnoreFailureAsync(input));
        }

        internal ChildProcess Child { get; }

        internal Task StandardOutput { get; }

        internal Task StandardError { get; }

        internal Task All { get; }
    }
}

internal sealed class ProcessPlatform
{
    internal static readonly ProcessPlatform Current = new(
        OperatingSystem.IsWindows(),
        File.Exists,
        Environment.GetEnvironmentVariable);

    internal ProcessPlatform(
        bool isWindows,
        Func<string, bool> fileExists,
        Func<string, string?> getEnvironmentVariable)
    {
        IsWindows = isWindows;
        FileExists = fileExists;
        GetEnvironmentVariable = getEnvironmentVariable;
    }

    internal bool IsWindows { get; }

    internal Func<string, bool> FileExists { get; }

    internal Func<string, string?> GetEnvironmentVariable { get; }

    internal char PathListSeparator => IsWindows ? ';' : ':';
}
