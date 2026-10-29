using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

    // Set by CommandApp and the hosting adapter: a human usage failure then ends with the command's usage line and
    // the help command to run.
    internal CommandUsageHint? UsageHint { get; init; }

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
        string? usageHint = UsageHint is not null && outcome.ExitCategory == CommandExitCategory.Usage &&
            (context.OutputMode == CommandOutputMode.Human || context.OutputShape == JsonOutputShape.Payload)
            ? UsageHint.Format(context.Path, textContext, context.Console)
            : null;
        if (context.OutputMode == CommandOutputMode.Json && context.OutputShape == JsonOutputShape.Payload)
        {
            string? payloadHint = ShowHostFailureHint && response.Fault?.Code == "RCLI5000"
                ? textContext.Resolve(CommandDebugOutput.HostFailureHintKey, CommandDebugOutput.HostFailureHint)
                : null;
            return WritePayloadAsync(context.Console, context.Culture, response, codec, outcome.HumanOutput, payloadHint, usageHint, cancellationToken);
        }

        if (ShowHostFailureHint && context.OutputMode == CommandOutputMode.Human && response.Fault?.Code == "RCLI5000")
        {
            string hint = textContext.Resolve(CommandDebugOutput.HostFailureHintKey, CommandDebugOutput.HostFailureHint);
            return WriteHumanAsync(context.Console, context.Culture, response, codec, cancellationToken, hint);
        }

        return context.OutputMode == CommandOutputMode.Human &&
            !outcome.Succeeded &&
            outcome.HumanOutput is { Length: > 0 } humanOutput
            ? DispatchFailureHumanOutputAsync(
                context.Console,
                context.Culture,
                response,
                codec,
                humanOutput,
                cancellationToken)
            : DispatchCoreAsync(
                context.OutputMode,
                context.Console,
                context.Culture,
                response,
                codec,
                cancellationToken,
                usageHint);
    }

    // A JSON output alias of payload shape: standard output receives the bare document, standard error the human
    // diagnostics and fault, or the command's own failure text in place of the fault line. The document is
    // serialized before anything is written, so a serialization failure leaves both streams untouched.
    private static async ValueTask WritePayloadAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        string? humanOutput,
        string? faultHint,
        string? usageHint,
        CancellationToken cancellationToken)
    {
        if (response.Succeeded)
        {
            byte[]? document = string.Equals(codec.PayloadType, CommandResultCodecs.Unit.PayloadType, StringComparison.Ordinal)
                ? null
                : SerializePayload(response.Payload!, codec.TypeInfo);
            await WriteDiagnosticsAsync(console, response.Diagnostics, cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                await console.WriteOutBytesAsync(document, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (response.FailureData is { } failureData)
        {
            await console.WriteOutBytesAsync(failureData.PayloadDocument(), cancellationToken).ConfigureAwait(false);
        }

        // Standard output belongs to the document, so the command's human failure text, which replaces the
        // fault line as a legacy contract's own error text, goes to standard error after the diagnostics.
        if (humanOutput is { Length: > 0 })
        {
            await console.WriteErrorAsync((BuildDiagnostics(response.Diagnostics) + humanOutput).AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteHumanAsync(console, culture, response, codec, cancellationToken, faultHint, usageHint).ConfigureAwait(false);
    }

    private static byte[] SerializePayload<T>(T payload, JsonTypeInfo<T> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = CommandJsonEncoding.Encoder }))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
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
        await DispatchCoreAsync(
            CommandOutputMode.Human,
            console,
            culture,
            response,
            codec,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a response through the supplied console using the selected output mode.</summary>
    /// <param name="mode">The output mode.</param>
    /// <param name="console">The invocation-local console.</param>
    /// <param name="culture">The presentation culture.</param>
    /// <param name="response">The response to write.</param>
    /// <param name="codec">The payload codec.</param>
    /// <param name="textContext">
    /// Optional text resolution for framework text in human output; machine identities and the sanitizer are
    /// unchanged. English text when null.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public static ValueTask DispatchAsync<T>(
        CommandOutputMode mode,
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CommandTextContext? textContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return DispatchCoreAsync(mode, console, culture, textContext is null ? response : textContext.Localize(response), codec, cancellationToken);
    }

    // As DispatchAsync, with the lines a usage error ends with in human output.
    internal static ValueTask DispatchAsync<T>(
        CommandOutputMode mode,
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CommandTextContext textContext,
        string? usageHint,
        CancellationToken cancellationToken) =>
        DispatchCoreAsync(mode, console, culture, textContext.Localize(response), codec, cancellationToken, usageHint);

    private static ValueTask DispatchCoreAsync<T>(
        CommandOutputMode mode,
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken,
        string? usageHint = null)
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
                cancellationToken,
                usageHint: usageHint),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static async ValueTask WriteHumanAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CancellationToken cancellationToken,
        string? faultHint = null,
        string? usageHint = null)
    {
        if (response.Succeeded)
        {
            await WriteDiagnosticsAsync(console, response.Diagnostics, cancellationToken).ConfigureAwait(false);
            await codec.WriteHumanAsync(response.Payload!, console, culture, cancellationToken).ConfigureAwait(false);
            return;
        }

        var text = new StringBuilder(BuildDiagnostics(response.Diagnostics));

        CommandFault fault = CommandFaultSanitizer.Sanitize(response.Fault!);
        if (!ContainsFaultDiagnostic(response.Diagnostics, fault))
        {
            text.Append(response.HumanFaultMessage ?? fault.Message);
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

        text.Append(usageHint);
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

    // Human output writes each diagnostic and the fault as its message alone, with the line breaks the author wrote.
    // The code is for programs, which read it from JSON output; people read the message, and a code they can look up
    // comes with its help link.
    private static string BuildDiagnostics(IReadOnlyList<CommandDiagnostic> diagnostics)
    {
        var text = new StringBuilder();
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            text.Append(CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message)
                ? "The diagnostic content was redacted."
                : CommandFaultSanitizer.SanitizeRequiredHumanText(diagnostic.Message));
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
