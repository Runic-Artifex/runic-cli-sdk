using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

// Developer-only exception output. Public faults stay sanitized; with a debugger attached,
// DOTNET_ENVIRONMENT=Development or RUNIC_COMMANDLINE_DEBUG=1 the observed exceptions are also
// written to stderr, which never carries the JSON frame.
internal sealed class CommandDebugOutput
{
    internal const string EnvironmentVariableName = "RUNIC_COMMANDLINE_DEBUG";
    internal const string HostFailureHintKey = "faults.RCLI5000.hint";
    internal const string HostFailureHint = "Set RUNIC_COMMANDLINE_DEBUG=1 to write the exception to stderr.";
    private readonly List<Exception> _pending = [];

    internal static bool IsEnabled(Func<string, string?> getEnvironmentVariable) =>
        Debugger.IsAttached ||
        string.Equals(getEnvironmentVariable(EnvironmentVariableName), "1", StringComparison.Ordinal) ||
        string.Equals(getEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

    internal Action<Exception> Observe(Action<Exception>? next) => exception =>
    {
        lock (_pending) _pending.Add(exception);
        next?.Invoke(exception);
    };

    internal async ValueTask FlushAsync(ICommandConsole console, CancellationToken cancellationToken)
    {
        Exception[] exceptions;
        lock (_pending)
        {
            exceptions = [.. _pending];
            _pending.Clear();
        }
        foreach (Exception exception in exceptions)
        {
            try
            {
                string text = "Runic.CommandLine observed an exception (" + EnvironmentVariableName + "/Development output):\n" + exception + "\n";
                await console.WriteErrorAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception writeException) when (writeException is not (OutOfMemoryException or AccessViolationException))
            {
                // Debug output is best effort and never changes the command result.
            }
        }
    }

    // Writes pending exceptions before the outcome, so the trace precedes the RCLI5000 line.
    internal sealed class FlushingSink(ICommandOutcomeSink inner, CommandDebugOutput debug) : ICommandOutcomeSink
    {
        public async ValueTask WriteAsync<T>(CommandDescriptor command, CommandExecutionContext context, CommandOutcome<T> outcome, ICommandResultCodec<T> codec,
            int exitCode, IReadOnlyList<CommandDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            await debug.FlushAsync(context.Console, CancellationToken.None).ConfigureAwait(false);
            await inner.WriteAsync(command, context, outcome, codec, exitCode, diagnostics, cancellationToken).ConfigureAwait(false);
        }
    }
}
