using System;
using System.Collections.Generic;
using System.Threading;

namespace Runic.CommandLine.Processes;

/// <summary>Contains a bounded, sanitized process execution result.</summary>
public sealed class ProcessResult
{
    internal ProcessResult(
        ProcessState state,
        int? exitCode,
        ProcessOutput standardOutput,
        ProcessOutput standardError,
        ProcessStartFailureCategory? startFailureCategory,
        CommandFault? fault,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        State = state;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        StartFailureCategory = startFailureCategory;
        Fault = fault;
        StartedAt = startedAt;
        EndedAt = endedAt;
    }

    /// <summary>Gets the terminal state.</summary>
    public ProcessState State { get; }

    /// <summary>Gets the child exit code when the child exited normally.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets captured standard output and its bounds metadata.</summary>
    public ProcessOutput StandardOutput { get; }

    /// <summary>Gets captured standard error and its bounds metadata.</summary>
    public ProcessOutput StandardError { get; }

    /// <summary>Gets a sanitized process-start failure category.</summary>
    public ProcessStartFailureCategory? StartFailureCategory { get; }

    /// <summary>Gets a stable consumer-safe fault for rejection or start failure.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Gets the UTC timestamp when request execution began.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets the UTC completion timestamp.</summary>
    public DateTimeOffset EndedAt { get; }

    /// <summary>Gets the elapsed wall-clock duration.</summary>
    public TimeSpan Duration => EndedAt - StartedAt;

    /// <summary>
    /// Throws unless the child exited and both captures are complete, for callers that treat anything else as a
    /// failure. The exit code is not checked.
    /// </summary>
    /// <param name="cancellationToken">The token reported by the exception for a cancelled request.</param>
    /// <returns>This result.</returns>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    /// <exception cref="ProcessException">
    /// The request was rejected, failed to start or to be observed, or timed out; or a capture was truncated at its
    /// limit or cut off after the drain grace period (<see cref="ProcessFaultCodes.OutputIncomplete"/>).
    /// </exception>
    public ProcessResult EnsureCompleted(CancellationToken cancellationToken = default)
    {
        switch (State)
        {
            case ProcessState.Exited:
                ThrowIfIncomplete(StandardOutput, "standardOutput");
                ThrowIfIncomplete(StandardError, "standardError");
                return this;
            case ProcessState.Cancelled:
                throw new OperationCanceledException("The process request was cancelled.", cancellationToken);
            case ProcessState.TimedOut:
                throw new ProcessException(this, new CommandFault(
                    ProcessFaultCodes.TimedOut,
                    "The process did not exit before its timeout."));
            default:
                throw new ProcessException(this, Fault ?? new CommandFault(
                    ProcessFaultCodes.ExecutionFailed,
                    "The process request did not complete."));
        }
    }

    private void ThrowIfIncomplete(ProcessOutput output, string stream)
    {
        if (!output.IsTruncated && !output.DrainTimedOut)
        {
            return;
        }

        throw new ProcessException(this, new CommandFault(
            ProcessFaultCodes.OutputIncomplete,
            output.IsTruncated
                ? "The process wrote more output than its capture limit."
                : "A descendant process held the output pipe open after the process exited.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stream"] = stream,
                ["reason"] = output.IsTruncated ? "truncated" : "drainTimedOut",
            }));
    }
}

/// <summary>Contains one decoded, bounded process output channel.</summary>
public sealed class ProcessOutput
{
    internal ProcessOutput(string text, long observedByteCount, bool isTruncated, bool drainTimedOut)
    {
        Text = text;
        ObservedByteCount = observedByteCount;
        IsTruncated = isTruncated;
        DrainTimedOut = drainTimedOut;
    }

    /// <summary>Gets retained output decoded with the request encoding.</summary>
    public string Text { get; }

    /// <summary>Gets the total number of bytes observed while draining the channel.</summary>
    public long ObservedByteCount { get; }

    /// <summary>Gets a value indicating whether observed bytes exceeded the retained-byte cap.</summary>
    public bool IsTruncated { get; }

    /// <summary>
    /// Gets a value indicating whether the channel was still open when the drain grace period expired,
    /// for example because a descendant inherited the pipe. The captured text may then be incomplete.
    /// </summary>
    public bool DrainTimedOut { get; }
}
