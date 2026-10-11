using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Dispatches semantic command responses to human or machine presentation.</summary>
public sealed class CommandOutputDispatcher : ICommandOutcomeSink
{
    /// <summary>Gets optional text resolution for execution diagnostics. Culture comes from the invocation context.</summary>
    public ICommandTextResolver? TextResolver { get; init; }

    // Set by CommandApp when nothing observes exceptions: the human RCLI5000 line then says how to see them.
    internal bool ShowHostFailureHint { get; init; }

    /// <inheritdoc />
    public ValueTask WriteAsync<T>(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<T> outcome,
        ICommandResultCodec<T> codec,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(diagnostics);

        string path = context.Path.Count == 0 ? command.Name : context.Path.ToString();
        CommandResponse<T> response = CommandResponse.FromOutcome(
            context.CorrelationId,
            path,
            exitCode,
            codec.PayloadType,
            outcome,
            diagnostics);

        var textContext = new CommandTextContext(context.Culture, TextResolver);
        response = textContext.Localize(response);
        if (ShowHostFailureHint && context.OutputMode == CommandOutputMode.Human && response.Fault?.Code == "RCLI5000")
        {
            string hint = textContext.Resolve(CommandDebugOutput.HostFailureHintKey, CommandDebugOutput.HostFailureHint);
            return WriteHumanAsync(context.Console, context.Culture, response, codec, cancellationToken, hint);
        }

        return context.OutputMode == CommandOutputMode.Human &&
            !outcome.IsSuccess &&
            outcome.HumanOutput is { Length: > 0 } humanOutput
            ? DispatchFailureHumanOutputAsync(
                context.Console,
                context.Culture,
                response,
                codec,
                humanOutput,
                cancellationToken)
            : DispatchAsync(
                context.OutputMode,
                context.Console,
                context.Culture,
                response,
                codec,
                cancellationToken);
    }

    private static async ValueTask DispatchFailureHumanOutputAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        string humanOutput,
        CancellationToken cancellationToken)
    {
        await console.WriteOutAsync(humanOutput.AsMemory(), cancellationToken).ConfigureAwait(false);
        await DispatchAsync(
            CommandOutputMode.Human,
            console,
            culture,
            response,
            codec,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a response through the supplied console using the selected output mode.</summary>
    public static ValueTask DispatchAsync<T>(
        CommandOutputMode mode,
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(codec);

        return mode switch
        {
            CommandOutputMode.Json => CommandJsonEnvelopeWriter.WriteAsync(
                console,
                response,
                codec,
                cancellationToken),
            CommandOutputMode.Human => WriteHumanAsync(
                console,
                culture,
                response,
                codec,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>Writes a response with explicit text resolution while preserving machine identities and the existing sanitizer.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The required CommandTextContext parameter keeps calls unambiguous; the earlier overload is unchanged.")]
    public static ValueTask DispatchAsync<T>(CommandOutputMode mode, ICommandConsole console, CultureInfo culture,
        CommandResponse<T> response, ICommandResultCodec<T> codec, CommandTextContext textContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(textContext);
        ArgumentNullException.ThrowIfNull(response);
        return DispatchAsync(mode, console, culture, textContext.Localize(response), codec, cancellationToken);
    }

    private static async ValueTask WriteHumanAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken,
        string? faultHint = null)
    {
        if (response.Success)
        {
            await WriteDiagnosticsAsync(console, response.Diagnostics, cancellationToken).ConfigureAwait(false);
            await codec.WriteHumanAsync(response.Payload!, console, culture, cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = new StringBuilder(BuildDiagnostics(response.Diagnostics));

        CommandFault fault = CommandFaultSanitizer.Sanitize(response.Fault!);
        if (!ContainsFaultDiagnostic(response.Diagnostics, fault))
        {
            text.Append(fault.Code);
            text.Append(": ");
            text.Append(fault.Message);
            if (faultHint is not null && !CommandFaultSanitizer.ContainsTechnicalContent(faultHint))
            {
                text.Append(' ');
                text.Append(CommandFaultSanitizer.SanitizeRequiredText(faultHint));
            }

            text.Append('\n');
        }

        // A diagnostic that describes the fault may already have written the same link.
        if (fault.HelpUri is { } helpUri && !HasHelpLink(response.Diagnostics, fault.Code, helpUri))
        {
            AppendHelpLink(text, fault.Code, helpUri);
        }

        await console.WriteErrorAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask WriteDiagnosticsAsync(
        ICommandConsole console,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string text = BuildDiagnostics(diagnostics);
        return text.Length == 0
            ? ValueTask.CompletedTask
            : console.WriteErrorAsync(text.AsMemory(), cancellationToken);
    }

    private static string BuildDiagnostics(IReadOnlyList<CommandDiagnostic> diagnostics)
    {
        var text = new StringBuilder();
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            text.Append(diagnostic.Code);
            text.Append(": ");
            text.Append(CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message)
                ? "The diagnostic content was redacted."
                : CommandFaultSanitizer.SanitizeRequiredText(diagnostic.Message));
            text.Append('\n');
            if (diagnostic.HelpUri is { } helpUri)
            {
                AppendHelpLink(text, diagnostic.Code, helpUri);
            }
        }

        return text.ToString();
    }

    // The link follows the line it documents, as the Runic SDK tools write it. It is the author's
    // validated https address, so it is written without the redaction that messages receive.
    private static void AppendHelpLink(StringBuilder text, string code, Uri helpUri) =>
        text.Append("Help for ").Append(code).Append(": ").Append(helpUri.AbsoluteUri).Append('\n');

    private static bool HasHelpLink(IReadOnlyList<CommandDiagnostic> diagnostics, string code, Uri helpUri)
    {
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            // Uri.Equals ignores fragments, which name the catalog entry, so compare the full text.
            if (string.Equals(diagnostic.Code, code, StringComparison.Ordinal) &&
                string.Equals(diagnostic.HelpUri?.AbsoluteUri, helpUri.AbsoluteUri, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsFaultDiagnostic(
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CommandFault fault)
    {
        // The fault was sanitized when the response was created, so its raw
        // message is gone. A redacted fault message and a redacted diagnostic
        // message with the same code describe the same failure; the redacted
        // diagnostic line already reports it.
        bool faultRedacted = string.Equals(
            fault.Message, CommandFaultSanitizer.RedactedFaultMessage, StringComparison.Ordinal);
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            if (!string.Equals(diagnostic.Code, fault.Code, StringComparison.Ordinal))
            {
                continue;
            }

            bool diagnosticRedacted = CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message);
            string message = diagnosticRedacted
                ? "The diagnostic content was redacted."
                : CommandFaultSanitizer.SanitizeRequiredText(diagnostic.Message);
            diagnosticRedacted |= string.IsNullOrWhiteSpace(CommandFaultSanitizer.SanitizeText(diagnostic.Message));
            if (string.Equals(message, fault.Message, StringComparison.Ordinal) ||
                (faultRedacted && diagnosticRedacted))
            {
                return true;
            }
        }

        return false;
    }
}
