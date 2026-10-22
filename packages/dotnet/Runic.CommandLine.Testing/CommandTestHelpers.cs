using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Runic.CommandLine.Testing;

/// <summary>Creates deterministic parsing inputs without mutating process environment.</summary>
public static class CommandTestEnvironment
{
    /// <summary>
    /// Captures parse settings as <see cref="CommandApp"/> captures them from the process environment, but from the
    /// supplied variables only: <c>RUNIC_COMMANDLINE_OUTPUT</c> selects the output mode, and
    /// <c>RUNIC_COMMANDLINE_NO_INPUT</c> and option environment fallbacks read the same variables.
    /// </summary>
    /// <param name="variables">The environment variables, or none; the process environment is never read.</param>
    /// <returns>Settings for <see cref="CommandTestInvocation.Parse(CommandCatalog, ParseSettings, string[])"/> or <see cref="CommandApp.ParseSettings"/>.</returns>
    public static ParseSettings Capture(IReadOnlyDictionary<string, string?>? variables = null)
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (variables is not null) foreach (KeyValuePair<string, string?> entry in variables) snapshot.Add(entry.Key, entry.Value);
        return new ParseSettings(snapshot.GetValueOrDefault(CommandOutputClassifier.EnvironmentVariableName))
        {
            GetEnvironmentVariable = name => snapshot.GetValueOrDefault(name),
        };
    }
}

/// <summary>Creates deterministic parsed invocations for command tests.</summary>
public static class CommandTestInvocation
{
    /// <summary>Parses arguments and returns the successful invocation.</summary>
    public static ParsedInvocation Parse(CommandCatalog catalog, ParseSettings settings, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(args);
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, settings);
        return outcome.Invocation ?? throw new InvalidOperationException("The supplied test arguments did not produce an invocation.");
    }

    /// <summary>Parses arguments with the default captured settings.</summary>
    public static ParsedInvocation Parse(CommandCatalog catalog, params string[] args) => Parse(catalog, ParseSettings.Default, args);
}

/// <summary>Reads the stable command response envelope in tests.</summary>
public static class CommandTestEnvelope
{
    /// <summary>Parses exactly one JSON command response frame.</summary>
    public static JsonDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        int lineFeedCount = 0;
        foreach (char character in json) if (character == '\n') lineFeedCount++;
        if (json.Length < 2 || !json.EndsWith('\n') || lineFeedCount != 1 || char.IsWhiteSpace(json[0]) || char.IsWhiteSpace(json[^2])) throw new ArgumentException("A command JSON frame must be one unpadded JSON value with exactly one terminal LF.", nameof(json));
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// Reads exactly one <c>runic.commandline/1</c> response frame strictly and deserializes its payload.
    /// </summary>
    /// <remarks>
    /// The frame is validated by <see cref="CommandJsonEnvelopeReader.Read{T}"/>: framing, size and depth limits,
    /// every required member and its JSON type, duplicate members, and the success and failure invariants. Unlike
    /// that reader, which ignores unknown members as the protocol requires of clients, this method also rejects any
    /// member the protocol does not define on the envelope, the fault, the fault's <c>data</c> and each diagnostic,
    /// with the error kind <c>unknown-member</c>. The payload type is the one the frame declares; compare
    /// <see cref="CommandResponse{T}.PayloadType"/> with the expected identity. The payload is deserialized with
    /// <paramref name="typeInfo"/> only, so its own unknown members follow that metadata.
    /// </remarks>
    /// <param name="json">The captured standard output: one compact JSON object followed by exactly one LF.</param>
    /// <param name="typeInfo">Source-generated metadata for the success payload.</param>
    /// <returns>The validated response, with the typed payload on success and the fault on failure.</returns>
    /// <exception cref="CommandProtocolException">The frame is not a valid, strict response envelope.</exception>
    public static CommandResponse<T> Read<T>(string json, JsonTypeInfo<T> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(typeInfo);
        byte[] frame;
        try
        {
            frame = StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException exception)
        {
            throw new CommandProtocolException("invalid-utf8", "The command response text contains an unpaired surrogate and has no UTF-8 encoding.", exception);
        }

        CommandResponse<T> response = CommandJsonEnvelopeReader.Read(frame, DeclaredPayloadType(frame), typeInfo);
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1), new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RejectUnknownMembers(root, EnvelopeMembers, "response");
        if (root.GetProperty("fault") is { ValueKind: JsonValueKind.Object } fault)
        {
            RejectUnknownMembers(fault, FaultMembers, "fault");
            if (fault.TryGetProperty("data", out JsonElement data)) RejectUnknownMembers(data, FailureDataMembers, "fault.data");
        }

        foreach (JsonElement diagnostic in root.GetProperty("diagnostics").EnumerateArray()) RejectUnknownMembers(diagnostic, DiagnosticMembers, "diagnostic");
        return response;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] EnvelopeMembers = ["protocol", "requestId", "command", "success", "exitCode", "payloadType", "payload", "fault", "diagnostics"];
    private static readonly string[] FaultMembers = ["code", "message", "details", "retryable", "helpUri", "data"];
    private static readonly string[] FailureDataMembers = ["type", "payload"];
    private static readonly string[] DiagnosticMembers = ["code", "kind", "commandPath", "messageKey", "message", "phase", "severity", "tokenIndex", "arguments", "helpUri"];

    // A well-formed identity is needed to call the core reader; it checks the declared value itself, so any frame
    // without one is read against a placeholder and fails there with the core reader's error.
    private static string DeclaredPayloadType(byte[] frame)
    {
        const string Placeholder = "runic.commandline.testing/1";
        if (frame.Length < 2) return Placeholder;
        try
        {
            using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1), new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("payloadType", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && IsPayloadType(value.GetString()!)
                ? value.GetString()!
                : Placeholder;
        }
        catch (JsonException)
        {
            return Placeholder;
        }
    }

    // [a-z][a-z0-9.-]*/[1-9][0-9]*, 3 to 128 ASCII bytes (specs/command-line/protocol/README.md).
    private static bool IsPayloadType(string value)
    {
        int slash = value.IndexOf('/', StringComparison.Ordinal);
        if (value.Length is < 3 or > 128 || slash < 1 || slash == value.Length - 1 || value[0] is < 'a' or > 'z' || value[slash + 1] == '0') return false;
        for (int index = 1; index < slash; index++) if (value[index] is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')) return false;
        for (int index = slash + 1; index < value.Length; index++) if (value[index] is < '0' or > '9') return false;
        return true;
    }

    private static void RejectUnknownMembers(JsonElement element, string[] known, string owner)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Array.IndexOf(known, property.Name) < 0) throw new CommandProtocolException("unknown-member", $"The {owner} contains the member '{property.Name}', which runic.commandline/1 does not define.");
        }
    }
}
