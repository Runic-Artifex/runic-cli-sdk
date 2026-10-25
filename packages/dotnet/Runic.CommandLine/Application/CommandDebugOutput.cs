using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

// Developer-only exception output. Public faults stay sanitized; with a debugger attached,
// DOTNET_ENVIRONMENT=Development or RUNIC_COMMANDLINE_DEBUG=1 the observed exceptions are also
// written to stderr, which never carries the JSON frame. The text keeps its line breaks and tabs, but
// not other control characters or the formatting characters that reorder or hide text, so an exception
// message cannot drive the terminal. It is not redacted: it can contain paths and secrets.
internal sealed class CommandDebugOutput
{
    internal const string EnvironmentVariableName = "RUNIC_COMMANDLINE_DEBUG";
    internal const string HostFailureHintKey = CommandTextKeys.FaultHostFailureHint;
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
                string text = "Runic.CommandLine observed an exception (" + EnvironmentVariableName + "/Development output):\n" + SanitizeTrace(exception.ToString()) + "\n";
                await console.WriteErrorAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception writeException) when (writeException is not (OutOfMemoryException or AccessViolationException))
            {
                // Debug output is best effort and never changes the command result.
            }
        }
    }

    // Removes C0 and C1 control characters other than LF and tab, and the formatting characters that reorder or
    // hide text. CR is removed too, so a Windows CRLF stack trace becomes LF lines.
    internal static string SanitizeTrace(string value)
    {
        System.Text.StringBuilder? builder = null;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool pair = char.IsSurrogatePair(value, index);
            bool remove = (char.IsControl(character) && character is not '\n' and not '\t') ||
                CommandFaultSanitizer.IsInvisibleFormatCharacter(pair ? char.ConvertToUtf32(character, value[index + 1]) : character);
            if (remove)
            {
                builder ??= new System.Text.StringBuilder(value, 0, index, value.Length);
                if (pair) index++;
                continue;
            }

            builder?.Append(character);
        }

        return builder?.ToString() ?? value;
    }

    // Writes pending exceptions before the outcome, so the trace precedes the RCLI5000 line.
    internal sealed class FlushingSink(ICommandOutcomeSink inner, CommandDebugOutput debug) : ICommandOutcomeSink
    {
        // An outcome presenter writes in place of the sink, so the trace is flushed before it too.
        internal ValueTask FlushAsync(ICommandConsole console) => debug.FlushAsync(console, CancellationToken.None);

        public async ValueTask WriteAsync<T>(CommandDescriptor command, CommandExecutionContext context, CommandOutcome<T> outcome, ICommandResultCodec<T> codec,
            int exitCode, IReadOnlyList<CommandDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            await debug.FlushAsync(context.Console, CancellationToken.None).ConfigureAwait(false);
            await inner.WriteAsync(command, context, outcome, codec, exitCode, diagnostics, cancellationToken).ConfigureAwait(false);
        }
    }
}
