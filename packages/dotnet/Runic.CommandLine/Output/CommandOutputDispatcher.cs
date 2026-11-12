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

    /// <summary>
    /// Gets how JSON output treats outcomes without a document, standard error and non-ASCII text.
    /// <see cref="CommandApp"/> sets it from <see cref="CommandApp.JsonOutput"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public CommandJsonOutputOptions JsonOutput
    {
        get => _jsonOutput;
        init => _jsonOutput = value ?? throw new ArgumentNullException(nameof(value));
    }

    private readonly CommandJsonOutputOptions _jsonOutput = CommandJsonOutputOptions.Default;

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
        CommandJsonOutputOptions json = JsonOutput;
        ICommandConsole console = json.QuietFor(context.OutputMode) ? new StandardErrorDiscardingConsole(context.Console) : context.Console;
        string? usageHint = UsageHint is not null && outcome.ExitCategory == CommandExitCategory.Usage &&
            (context.OutputMode == CommandOutputMode.Human || context.OutputShape == JsonOutputShape.Payload)
            ? UsageHint.Format(context.Path, textContext, context.Console)
            : null;
        if (context.OutputMode == CommandOutputMode.Json && context.OutputShape == JsonOutputShape.Payload)
        {
            string? payloadHint = ShowHostFailureHint && response.Fault?.Code == "RCLI5000"
                ? textContext.Resolve(CommandDebugOutput.HostFailureHintKey, CommandDebugOutput.HostFailureHint)
                : null;
            return WritePayloadAsync(console, context.Culture, response, codec, textContext, outcome.HumanOutput, payloadHint, usageHint, json, cancellationToken);
        }

        if (ShowHostFailureHint && context.OutputMode == CommandOutputMode.Human && response.Fault?.Code == "RCLI5000")
        {
            string hint = textContext.Resolve(CommandDebugOutput.HostFailureHintKey, CommandDebugOutput.HostFailureHint);
            return WriteHumanAsync(context.Console, context.Culture, response, codec, textContext, cancellationToken, hint);
        }

        return context.OutputMode == CommandOutputMode.Human &&
            !outcome.Succeeded &&
            outcome.HumanOutput is { Length: > 0 } humanOutput
            ? DispatchFailureHumanOutputAsync(
                context.Console,
                context.Culture,
                response,
                codec,
                textContext,
                humanOutput,
                cancellationToken)
            : DispatchCoreAsync(
                context.OutputMode,
                console,
                context.Culture,
                response,
                codec,
                textContext,
                cancellationToken,
                usageHint,
                json.Escaping);
    }

    // A JSON output alias of payload shape: standard output receives the bare document, standard error the human
    // diagnostics and fault, or the command's own failure text in place of the fault line. The document is
    // serialized before anything is written, so a serialization failure leaves both streams untouched. With
    // DocumentEveryOutcome, a failure without declared data writes the envelope in place of no document.
    private static async ValueTask WritePayloadAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CommandTextContext text,
        string? humanOutput,
        string? faultHint,
        string? usageHint,
        CommandJsonOutputOptions json,
        CancellationToken cancellationToken)
    {
        if (response.Succeeded)
        {
            byte[]? document = !json.DocumentEveryOutcome &&
                string.Equals(codec.PayloadType, CommandResultCodecs.Unit.PayloadType, StringComparison.Ordinal)
                ? null
                : SerializePayload(response.Payload!, codec.TypeInfo, json.Escaping);
            await WriteDiagnosticsAsync(console, response.Diagnostics, text, cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                await console.WriteOutBytesAsync(document, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (response.FailureData is { } failureData)
        {
            await console.WriteOutBytesAsync(Escape(failureData.PayloadDocument(), json.Escaping), cancellationToken).ConfigureAwait(false);
        }
        else if (json.DocumentEveryOutcome)
        {
            await CommandJsonEnvelopeWriter.WriteAsync(console, response, codec, json.Escaping, cancellationToken, text).ConfigureAwait(false);
        }

        // Standard output belongs to the document, so the command's human failure text, which replaces the
        // fault line as a legacy contract's own error text, goes to standard error after the diagnostics.
        if (humanOutput is { Length: > 0 })
        {
            await console.WriteErrorAsync((BuildDiagnostics(response.Diagnostics, text) + humanOutput).AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteHumanAsync(console, culture, response, codec, text, cancellationToken, faultHint, usageHint).ConfigureAwait(false);
    }

    // A payload-shape document for a successful framework request (help or version text) under DocumentEveryOutcome.
    internal static ValueTask WritePayloadDocumentAsync<T>(
        ICommandConsole console,
        T payload,
        JsonTypeInfo<T> typeInfo,
        CommandJsonEscaping escaping,
        CancellationToken cancellationToken) =>
        console.WriteOutBytesAsync(SerializePayload(payload, typeInfo, escaping), cancellationToken);

    internal static byte[] Escape(byte[] json, CommandJsonEscaping escaping) =>
        escaping == CommandJsonEscaping.Ascii ? CommandJsonAscii.Escape(json) : json;

    private static byte[] SerializePayload<T>(T payload, JsonTypeInfo<T> typeInfo, CommandJsonEscaping escaping)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = CommandJsonEncoding.Encoder }))
        {
            JsonSerializer.Serialize(writer, payload, typeInfo);
        }

        buffer.Write("\n"u8);
        return Escape(buffer.WrittenSpan.ToArray(), escaping);
    }

    private static async ValueTask DispatchFailureHumanOutputAsync<T>(
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CommandTextContext text,
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
            text,
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
        return DispatchCoreAsync(mode, console, culture, textContext is null ? response : textContext.Localize(response), codec, textContext, cancellationToken);
    }

    // As DispatchAsync, with the lines a usage error ends with in human output and the escaping of JSON output.
    internal static ValueTask DispatchAsync<T>(
        CommandOutputMode mode,
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CommandTextContext textContext,
        string? usageHint,
        CommandJsonEscaping escaping,
        CancellationToken cancellationToken) =>
        DispatchCoreAsync(mode, console, culture, textContext.Localize(response), codec, textContext, cancellationToken, usageHint, escaping);

    private static ValueTask DispatchCoreAsync<T>(
        CommandOutputMode mode,
        ICommandConsole console,
        CultureInfo culture,
        CommandResponse<T> response,
        ICommandResultCodec<T> codec,
        CommandTextContext? text,
        CancellationToken cancellationToken,
        string? usageHint = null,
        CommandJsonEscaping escaping = CommandJsonEscaping.Unicode)
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
                escaping,
                cancellationToken,
                text),
            CommandOutputMode.Human => WriteHumanAsync(
                console,
                culture,
                response,
                codec,
                text,
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
        CommandTextContext? text,
        CancellationToken cancellationToken,
        string? faultHint = null,
        string? usageHint = null)
    {
        if (response.Succeeded)
        {
            await WriteDiagnosticsAsync(console, response.Diagnostics, text, cancellationToken).ConfigureAwait(false);
            await codec.WriteHumanAsync(response.Payload!, console, culture, cancellationToken).ConfigureAwait(false);
            return;
        }

        var output = new StringBuilder(BuildDiagnostics(response.Diagnostics, text));

        CommandFault fault = CommandFaultSanitizer.Sanitize(response.Fault!, text);
        if (!ContainsFaultDiagnostic(response.Diagnostics, fault, text))
        {
            output.Append(response.HumanFaultMessage ?? fault.Message);
            if (faultHint is not null && !CommandFaultSanitizer.ContainsTechnicalContent(faultHint))
            {
                output.Append(' ');
                output.Append(CommandFaultSanitizer.SanitizeRequiredText(faultHint, text));
            }

            output.Append('\n');
        }

        // A diagnostic that describes the fault may already have written the same link.
        if (fault.HelpUri is { } helpUri && !HasHelpLink(response.Diagnostics, fault.Code, helpUri))
        {
            AppendHelpLink(output, fault.Code, helpUri, text);
        }

        output.Append(usageHint);
        await console.WriteErrorAsync(output.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static ValueTask WriteDiagnosticsAsync(
        ICommandConsole console,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CommandTextContext? textContext,
        CancellationToken cancellationToken)
    {
        string text = BuildDiagnostics(diagnostics, textContext);
        return text.Length == 0
            ? ValueTask.CompletedTask
            : console.WriteErrorAsync(text.AsMemory(), cancellationToken);
    }

    // Human output writes each diagnostic and the fault as its message alone, with the line breaks the author wrote.
    // The code is for programs, which read it from JSON output; people read the message, and a code they can look up
    // comes with its help link.
    private static string BuildDiagnostics(IReadOnlyList<CommandDiagnostic> diagnostics, CommandTextContext? textContext)
    {
        var text = new StringBuilder();
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            text.Append(CommandFaultSanitizer.DiagnosticMessage(diagnostic.Message, textContext, human: true));
            text.Append('\n');
            if (diagnostic.HelpUri is { } helpUri)
            {
                AppendHelpLink(text, diagnostic.Code, helpUri, textContext);
            }
        }

        return text.ToString();
    }

    // The link follows the line it documents, as the Runic SDK tools write it. It is the author's
    // validated https address, so it is written without the redaction that messages receive; the text
    // around it (hints.help-link) is sanitized like other resolved text and must keep the link.
    private static void AppendHelpLink(StringBuilder text, string code, Uri helpUri, CommandTextContext? textContext)
    {
        string link = helpUri.AbsoluteUri;
        string line = "Help for " + code + ": " + link;
        if (textContext?.Resolver is not null)
        {
            string resolved = textContext.Resolve(CommandTextKeys.HintHelpLink, line, [code, link]);
            int at = resolved.IndexOf(link, StringComparison.Ordinal);
            if (at >= 0 && resolved.IndexOf(link, at + link.Length, StringComparison.Ordinal) < 0)
            {
                string before = resolved[..at];
                string after = resolved[(at + link.Length)..];
                if (!CommandFaultSanitizer.ContainsTechnicalContent(before) && !CommandFaultSanitizer.ContainsTechnicalContent(after))
                {
                    line = CommandFaultSanitizer.SanitizeText(before) + link + CommandFaultSanitizer.SanitizeText(after);
                }
            }
        }

        text.Append(line).Append('\n');
    }

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
        CommandFault fault,
        CommandTextContext? text)
    {
        // The fault was sanitized when the response was created, so its raw
        // message is gone. A redacted fault message and a redacted diagnostic
        // message with the same code describe the same failure; the redacted
        // diagnostic line already reports it.
        bool faultRedacted = string.Equals(fault.Message, CommandFaultSanitizer.RedactedFaultMessage, StringComparison.Ordinal) ||
            (text is not null && string.Equals(fault.Message,
                text.RedactionNote(CommandTextKeys.RedactionFault, CommandFaultSanitizer.RedactedFaultMessage), StringComparison.Ordinal));
        foreach (CommandDiagnostic diagnostic in diagnostics)
        {
            if (!string.Equals(diagnostic.Code, fault.Code, StringComparison.Ordinal))
            {
                continue;
            }

            bool diagnosticRedacted = CommandFaultSanitizer.ContainsTechnicalContent(diagnostic.Message);
            string message = CommandFaultSanitizer.DiagnosticMessage(diagnostic.Message, text);
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
