using System;
using System.Collections.Generic;
using System.Text;
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

    private static readonly ProcessOutput EmptyOutput = new(string.Empty, 0);

    /// <summary>
    /// Creates the result of a child that exited, as <see cref="ProcessRunner"/> returns it, for example from a test
    /// double of <see cref="IProcessRunner"/>.
    /// </summary>
    /// <param name="exitCode">The child's exit code.</param>
    /// <param name="standardOutput">The captured standard output; empty by default.</param>
    /// <param name="standardError">The captured standard error; empty by default.</param>
    /// <param name="startedAt">The time request execution began.</param>
    /// <param name="duration">The elapsed time; <see cref="EndedAt"/> is <paramref name="startedAt"/> plus it.</param>
    /// <returns>An <see cref="ProcessState.Exited"/> result without a fault.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or ends after the latest time.</exception>
    public static ProcessResult Exited(
        int exitCode,
        ProcessOutput? standardOutput = null,
        ProcessOutput? standardError = null,
        DateTimeOffset startedAt = default,
        TimeSpan duration = default) =>
        Create(ProcessState.Exited, exitCode, standardOutput, standardError, null, null, startedAt, duration);

    /// <summary>
    /// Creates the result of a request whose timeout elapsed before the child exited, as <see cref="ProcessRunner"/>
    /// returns it. <see cref="EnsureCompleted"/> throws a <see cref="ProcessException"/> with
    /// <see cref="ProcessFaultCodes.TimedOut"/> for it.
    /// </summary>
    /// <param name="standardOutput">The standard output captured until the child was stopped; empty by default.</param>
    /// <param name="standardError">The standard error captured until the child was stopped; empty by default.</param>
    /// <param name="startedAt">The time request execution began.</param>
    /// <param name="duration">The elapsed time; <see cref="EndedAt"/> is <paramref name="startedAt"/> plus it.</param>
    /// <returns>A <see cref="ProcessState.TimedOut"/> result without an exit code or a fault.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or ends after the latest time.</exception>
    public static ProcessResult TimedOut(
        ProcessOutput? standardOutput = null,
        ProcessOutput? standardError = null,
        DateTimeOffset startedAt = default,
        TimeSpan duration = default) =>
        Create(ProcessState.TimedOut, null, standardOutput, standardError, null, null, startedAt, duration);

    /// <summary>
    /// Creates the result of a cancelled request, as <see cref="ProcessRunner"/> returns it.
    /// <see cref="EnsureCompleted"/> throws an <see cref="OperationCanceledException"/> for it.
    /// </summary>
    /// <param name="standardOutput">The standard output captured until the child was stopped; empty by default.</param>
    /// <param name="standardError">The standard error captured until the child was stopped; empty by default.</param>
    /// <param name="startedAt">The time request execution began.</param>
    /// <param name="duration">The elapsed time; <see cref="EndedAt"/> is <paramref name="startedAt"/> plus it.</param>
    /// <returns>A <see cref="ProcessState.Cancelled"/> result without an exit code or a fault.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or ends after the latest time.</exception>
    public static ProcessResult Cancelled(
        ProcessOutput? standardOutput = null,
        ProcessOutput? standardError = null,
        DateTimeOffset startedAt = default,
        TimeSpan duration = default) =>
        Create(ProcessState.Cancelled, null, standardOutput, standardError, null, null, startedAt, duration);

    /// <summary>
    /// Creates the result of a request that was rejected before start, as <see cref="ProcessRunner"/> returns it.
    /// The output is empty.
    /// </summary>
    /// <param name="fault">
    /// The rejection fault; by default <see cref="ProcessFaultCodes.ExecutableRejected"/> with the message the runner
    /// uses for a policy rejection. The runner reports <see cref="ProcessFaultCodes.ExecutableRejected"/>,
    /// <see cref="ProcessFaultCodes.WorkingDirectoryRejected"/>, <see cref="ProcessFaultCodes.InvalidPolicyDecision"/>,
    /// <see cref="ProcessFaultCodes.PolicyEvaluationFailed"/>, <see cref="ProcessFaultCodes.BatchFileRejected"/> or
    /// <see cref="ProcessFaultCodes.BatchFileArgumentRejected"/>.
    /// </param>
    /// <param name="startedAt">The time request execution began.</param>
    /// <param name="duration">The elapsed time; <see cref="EndedAt"/> is <paramref name="startedAt"/> plus it.</param>
    /// <returns>A <see cref="ProcessState.Rejected"/> result with a fault.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or ends after the latest time.</exception>
    public static ProcessResult Rejected(
        CommandFault? fault = null,
        DateTimeOffset startedAt = default,
        TimeSpan duration = default) =>
        Create(
            ProcessState.Rejected,
            null,
            null,
            null,
            null,
            fault ?? new CommandFault(ProcessFaultCodes.ExecutableRejected, PolicyRejectionMessage),
            startedAt,
            duration);

    /// <summary>
    /// Creates the result of a child that could not be started, as <see cref="ProcessRunner"/> returns it: the fault
    /// is <see cref="ProcessFaultCodes.StartFailed"/> with the category in its <c>category</c> detail, and the output
    /// is empty.
    /// </summary>
    /// <param name="category">The sanitized start failure category.</param>
    /// <param name="startedAt">The time request execution began.</param>
    /// <param name="duration">The elapsed time; <see cref="EndedAt"/> is <paramref name="startedAt"/> plus it.</param>
    /// <returns>A <see cref="ProcessState.StartFailed"/> result with a category and a fault.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The category is not defined, or the duration is negative or ends after the latest time.
    /// </exception>
    public static ProcessResult StartFailed(
        ProcessStartFailureCategory category,
        DateTimeOffset startedAt = default,
        TimeSpan duration = default)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "The category is not defined.");
        }

        return Create(
            ProcessState.StartFailed,
            null,
            null,
            null,
            category,
            CreateStartFault(category),
            startedAt,
            duration);
    }

    /// <summary>
    /// Creates the result of a started request whose lifecycle could not be observed safely, or whose output handler
    /// failed, as <see cref="ProcessRunner"/> returns it.
    /// </summary>
    /// <param name="fault">
    /// The fault; by default <see cref="ProcessFaultCodes.ExecutionFailed"/> with the message the runner uses. The
    /// runner reports <see cref="ProcessFaultCodes.ExecutionFailed"/> or
    /// <see cref="ProcessFaultCodes.OutputHandlerFailed"/>.
    /// </param>
    /// <param name="standardOutput">The captured standard output; empty by default.</param>
    /// <param name="standardError">The captured standard error; empty by default.</param>
    /// <param name="startedAt">The time request execution began.</param>
    /// <param name="duration">The elapsed time; <see cref="EndedAt"/> is <paramref name="startedAt"/> plus it.</param>
    /// <returns>A <see cref="ProcessState.ExecutionFailed"/> result with a fault and without an exit code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or ends after the latest time.</exception>
    public static ProcessResult ExecutionFailed(
        CommandFault? fault = null,
        ProcessOutput? standardOutput = null,
        ProcessOutput? standardError = null,
        DateTimeOffset startedAt = default,
        TimeSpan duration = default) =>
        Create(
            ProcessState.ExecutionFailed,
            null,
            standardOutput,
            standardError,
            null,
            fault ?? new CommandFault(ProcessFaultCodes.ExecutionFailed, ExecutionFailedMessage),
            startedAt,
            duration);

    internal const string PolicyRejectionMessage = "The process request is not permitted by the configured policy.";

    internal const string ExecutionFailedMessage = "The process lifecycle could not be observed safely.";

    internal static ProcessOutput Empty => EmptyOutput;

    internal static CommandFault CreateStartFault(ProcessStartFailureCategory category) =>
        new(
            ProcessFaultCodes.StartFailed,
            "The process could not be started.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["category"] = category.ToString(),
            });

    private static ProcessResult Create(
        ProcessState state,
        int? exitCode,
        ProcessOutput? standardOutput,
        ProcessOutput? standardError,
        ProcessStartFailureCategory? startFailureCategory,
        CommandFault? fault,
        DateTimeOffset startedAt,
        TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        if (duration > DateTimeOffset.MaxValue - startedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "The result would end after the latest time.");
        }

        return new ProcessResult(
            state,
            exitCode,
            standardOutput ?? EmptyOutput,
            standardError ?? EmptyOutput,
            startFailureCategory,
            fault,
            startedAt,
            startedAt + duration);
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
    /// <summary>
    /// Initializes a channel's capture, as <see cref="ProcessRunner"/> returns it, for example in a test double of
    /// <see cref="IProcessRunner"/>.
    /// </summary>
    /// <param name="text">The retained text.</param>
    /// <param name="observedByteCount">
    /// The total number of bytes observed; by default the length of <paramref name="text"/> in UTF-8, the default
    /// channel encoding. A truncated capture observed more bytes than it retained.
    /// </param>
    /// <param name="isTruncated">Whether the observed bytes exceeded the retained-byte cap.</param>
    /// <param name="drainTimedOut">Whether the channel was still open when the drain grace period expired.</param>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The observed byte count is negative.</exception>
    public ProcessOutput(
        string text,
        long? observedByteCount = null,
        bool isTruncated = false,
        bool drainTimedOut = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (observedByteCount is { } count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count, nameof(observedByteCount));
        }

        Text = text;
        ObservedByteCount = observedByteCount ?? Encoding.UTF8.GetByteCount(text);
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
