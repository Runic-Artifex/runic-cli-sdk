using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>
/// Writes the human output of one command's whole outcome, success or failure. Register it with
/// <see cref="CommandCatalogBuilder.PresentOutcome{T}"/>; JSON output continues to use the application's outcome sink.
/// </summary>
/// <typeparam name="T">The command result type.</typeparam>
/// <param name="presentation">The outcome, its exit code and diagnostics, and the invocation's context.</param>
/// <param name="cancellationToken">Cancels the write.</param>
public delegate ValueTask CommandOutcomePresenter<T>(CommandOutcomePresentation<T> presentation, CancellationToken cancellationToken);

/// <summary>The settled outcome of one invocation, as a <see cref="CommandOutcomePresenter{T}"/> receives it.</summary>
/// <typeparam name="T">The command result type.</typeparam>
public sealed class CommandOutcomePresentation<T>
{
    private readonly ICommandOutcomeSink _sink;
    private readonly ICommandResultCodec<T> _codec;

    internal CommandOutcomePresentation(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<T> outcome,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        ICommandOutcomeSink sink,
        ICommandResultCodec<T> codec)
    {
        Command = command;
        Context = context;
        Outcome = outcome;
        ExitCode = exitCode;
        Diagnostics = diagnostics;
        _sink = sink;
        _codec = codec;
    }

    /// <summary>Gets the command that ran.</summary>
    public CommandDescriptor Command { get; }

    /// <summary>
    /// Gets the invocation's context: its console, path, culture, text resolver and correlation identifier, and the
    /// same input state the handler had, so <c>new CommandPrompts(Context).IsInteractive</c> is false under
    /// <c>RUNIC_COMMANDLINE_NO_INPUT</c>. Its scoped services have already been disposed.
    /// </summary>
    public CommandExecutionContext Context { get; }

    /// <summary>Gets the outcome: the handler's, or the framework's for a binding failure, cancellation or host failure.</summary>
    public CommandOutcome<T> Outcome { get; }

    /// <summary>Gets the process exit code the invocation returns.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the binding and outcome diagnostics, in order.</summary>
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Writes the output the application's outcome sink writes for this outcome without the presenter, for example to
    /// keep the default output for successes and present only failures.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    public ValueTask WriteDefaultAsync(CancellationToken cancellationToken = default) =>
        _sink.WriteAsync(Command, Context, Outcome, _codec, ExitCode, Diagnostics, cancellationToken);
}
