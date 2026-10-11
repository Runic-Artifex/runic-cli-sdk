using System;
using System.Collections.Generic;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Runic.CommandLine;

/// <summary>An immutable, bounded snapshot of explicitly declared domain failure data.</summary>
/// <remarks>
/// This data is emitted only in the optional <c>fault.data</c> extension. Unlike
/// fault presentation text, application-owned fields are preserved exactly and
/// are not sanitized. Declare only fields intended for the command's consumer;
/// exclude exceptions, logs, secrets, and unrelated internal state.
/// </remarks>
public sealed class CommandFailureData
{
    /// <summary>The maximum UTF-8 JSON payload size, excluding the extension wrapper.</summary>
    public const int MaximumPayloadBytes = 65_536;

    /// <summary>The maximum number of nested payload objects and arrays.</summary>
    public const int MaximumPayloadDepth = 24;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly JsonElement _payload;
    private readonly byte[] _payloadUtf8;

    private CommandFailureData(string dataType, JsonElement payload, byte[] payloadUtf8)
    {
        Type = dataType;
        _payload = payload.Clone();
        _payloadUtf8 = payloadUtf8;
    }

    /// <summary>Gets the complete, independently versioned domain data identity.</summary>
    public string Type { get; }

    /// <summary>Declares an identity and closed JSON contract and snapshots the domain value.</summary>
    /// <remarks>
    /// Use application-owned source-generated metadata, such as
    /// <c>RecoveryJsonContext.Default.CloneRecovery</c>. Subsequent changes to
    /// <paramref name="value"/> cannot change the snapshot. Null is supported
    /// when permitted by the declared contract. Data is never truncated.
    /// </remarks>
    /// <exception cref="CommandProtocolException">
    /// The value exceeds a bound, contains invalid UTF-16 (an unpaired surrogate) in a
    /// string or property name, or cannot be serialized; the kind matches the reader's.
    /// </exception>
    public static CommandFailureData Create<T>(string dataType, T value, JsonTypeInfo<T> typeInfo)
    {
        CommandResponseValidation.ValidatePayloadType(dataType, nameof(dataType));
        ArgumentNullException.ThrowIfNull(typeInfo);

        var buffer = new CommandJsonBufferWriter(MaximumPayloadBytes,
            "failure-data-byte-limit", "The declared failure data exceeds its JSON byte limit.");
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = false,
                MaxDepth = MaximumPayloadDepth,
                SkipValidation = false,
                // The framework encoder alone would replace an unpaired surrogate with U+FFFD.
                Encoder = UnpairedSurrogateRejectingEncoder.Instance,
            }))
            {
                try
                {
                    JsonSerializer.Serialize(writer, value, typeInfo);
                }
                catch (Exception exception) when (writer.CurrentDepth >= MaximumPayloadDepth &&
                    (exception as InvalidOperationException ?? (exception as JsonException)?.InnerException as InvalidOperationException) is not null)
                {
                    // The writer refuses a container beyond MaxDepth (the serializer may
                    // wrap it); report the reader's kind for an over-deep payload.
                    throw DepthLimit();
                }
            }

            using JsonDocument document = JsonDocument.Parse(buffer.WrittenSpan.ToArray(),
                new JsonDocumentOptions { MaxDepth = MaximumPayloadDepth });
            return Read(dataType, document.RootElement);
        }
        catch (CommandProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            // Serializer, converter and encoding failures (unsupported types, invalid
            // UTF-16) remain protocol failures rather than generic host failures.
            throw InvalidData(exception);
        }
    }

    /// <summary>Decodes only a matching allow-listed identity using its closed JSON contract.</summary>
    /// <returns>False for an unknown identity, allowing the ordinary safe fault to be presented.</returns>
    /// <remarks>Required fields, nullability, and unknown members follow the supplied metadata's serializer settings.</remarks>
    /// <exception cref="CommandProtocolException">Matching data violates the selected JSON contract.</exception>
    public bool TryGet<T>(string expectedType, JsonTypeInfo<T> typeInfo, out T? value)
    {
        CommandResponseValidation.ValidatePayloadType(expectedType, nameof(expectedType));
        ArgumentNullException.ThrowIfNull(typeInfo);
        value = default;
        if (!string.Equals(Type, expectedType, StringComparison.Ordinal)) return false;

        try
        {
            value = _payload.Deserialize(typeInfo);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            throw ShapeMismatch(exception);
        }
    }

    internal static CommandFailureData Read(string dataType, JsonElement payload)
    {
        CommandResponseValidation.ValidatePayloadType(dataType, nameof(dataType));
        // Measure the compact value that Write emits, so insignificant whitespace
        // from an external producer does not count against the byte limit.
        byte[] compact = CompactPayload(payload);
        if (compact.Length > MaximumPayloadBytes)
            throw new CommandProtocolException("failure-data-byte-limit", "The declared failure data exceeds its JSON byte limit.");
        ValidatePayload(payload, 0);
        return new CommandFailureData(dataType, payload, compact);
    }

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("type", Type);
        writer.WritePropertyName("payload");
        // Re-encoding a raw Unicode value with the writer's default escaping
        // could inflate a valid bounded snapshot beyond its byte limit.
        writer.WriteRawValue(_payloadUtf8, skipInputValidation: false);
        writer.WriteEndObject();
    }

    private static byte[] CompactPayload(JsonElement payload)
    {
        byte[] bytes = StrictUtf8.GetBytes(payload.GetRawText());
        int written = 0;
        bool inString = false;
        bool escaped = false;
        foreach (byte value in bytes)
        {
            if (inString)
            {
                bytes[written++] = value;
                if (escaped) escaped = false;
                else if (value == (byte)'\\') escaped = true;
                else if (value == (byte)'"') inString = false;
            }
            else if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                bytes[written++] = value;
                if (value == (byte)'"') inString = true;
            }
        }

        if (written != bytes.Length) Array.Resize(ref bytes, written);
        return bytes;
    }

    private static void ValidatePayload(JsonElement element, int depth)
    {
        if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            depth++;
            if (depth > MaximumPayloadDepth)
                throw DepthLimit();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new CommandProtocolException("duplicate-property", "The declared failure data contains duplicate JSON members.");
                _ = StrictUtf8.GetByteCount(property.Name);
                ValidatePayload(property.Value, depth);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray()) ValidatePayload(item, depth);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            _ = StrictUtf8.GetByteCount(element.GetString()!);
        }
    }

    private static CommandProtocolException DepthLimit() =>
        new("failure-data-depth-limit", "The declared failure data exceeds its JSON depth limit.");

    private static CommandProtocolException InvalidData(Exception exception) =>
        new("invalid-failure-data", "The declared failure data does not contain a valid bounded JSON value.", exception);

    private static CommandProtocolException UnpairedSurrogate() =>
        new("invalid-failure-data", "The declared failure data contains invalid UTF-16 (an unpaired surrogate) and cannot be represented exactly.");

    private static CommandProtocolException ShapeMismatch(Exception exception) =>
        new("failure-data-shape-mismatch", "The failure data does not match its registered JSON contract.", exception);

    /// <summary>
    /// Escapes exactly as the response writer (<see cref="CommandJsonEncoding.Encoder"/>)
    /// but refuses unpaired surrogates instead of letting them become U+FFFD.
    /// </summary>
    /// <remarks>
    /// The writer passes every UTF-16 string value and property name, including
    /// dictionary keys, through <see cref="FindFirstCharacterToEncode"/> before
    /// escaping. Source-generated static property names are pre-encoded, and
    /// System.Text.Json already refuses invalid UTF-16 there.
    /// </remarks>
    private sealed class UnpairedSurrogateRejectingEncoder : JavaScriptEncoder
    {
        internal static readonly UnpairedSurrogateRejectingEncoder Instance = new();
        private static readonly JavaScriptEncoder Inner = CommandJsonEncoding.Encoder;

        public override int MaxOutputCharactersPerInputCharacter => Inner.MaxOutputCharactersPerInputCharacter;

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            RejectUnpairedSurrogates(new ReadOnlySpan<char>(text, textLength));
            return Inner.FindFirstCharacterToEncode(text, textLength);
        }

        public override OperationStatus Encode(ReadOnlySpan<char> source, Span<char> destination,
            out int charsConsumed, out int charsWritten, bool isFinalBlock = true)
        {
            // A non-final block may end with a high surrogate whose pair follows in the next block.
            RejectUnpairedSurrogates(!isFinalBlock && source.Length > 0 && char.IsHighSurrogate(source[^1]) ? source[..^1] : source);
            return Inner.Encode(source, destination, out charsConsumed, out charsWritten, isFinalBlock);
        }

        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten) =>
            Inner.TryEncodeUnicodeScalar(unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);

        public override bool WillEncode(int unicodeScalar) => Inner.WillEncode(unicodeScalar);

        // The writer validates UTF-8 input itself; delegate so escaping stays identical.
        public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text) =>
            Inner.FindFirstCharacterToEncodeUtf8(utf8Text);

        public override OperationStatus EncodeUtf8(ReadOnlySpan<byte> utf8Source, Span<byte> utf8Destination,
            out int bytesConsumed, out int bytesWritten, bool isFinalBlock = true) =>
            Inner.EncodeUtf8(utf8Source, utf8Destination, out bytesConsumed, out bytesWritten, isFinalBlock);

        private static void RejectUnpairedSurrogates(ReadOnlySpan<char> text)
        {
            int index = text.IndexOfAnyInRange('\uD800', '\uDFFF');
            while (index >= 0)
            {
                if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    throw UnpairedSurrogate();
                text = text[(index + 2)..];
                index = text.IndexOfAnyInRange('\uD800', '\uDFFF');
            }
        }
    }
}
