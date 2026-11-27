using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Runic.CommandLine.Conformance;

// The client's registered payload types: the manifest's sample payloads (protocol/manifest.json "samplePayloads"), as
// an application registers its own with source-generated metadata. The reader only decodes a payload through these.
internal sealed record StatusPayload(string? State);

internal sealed record ExportPayload(int Written, string OutputPath);

// Doubles, which the writer writes as .NET's Utf8JsonWriter does (protocol README, "Examples, writer cases and escaping
// profiles"). The members are in the order of the example.
internal sealed record MeasurementPayload(
    double One,
    double Fraction,
    double Negative,
    double Tenth,
    double Small,
    double Smaller,
    double Tiny,
    double Large,
    double Larger,
    double Huge,
    double Precise,
    double Maximum,
    double Minimum);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusPayload))]
[JsonSerializable(typeof(ExportPayload))]
[JsonSerializable(typeof(MeasurementPayload))]
[JsonSerializable(typeof(AdapterResult))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class PayloadJsonContext : JsonSerializerContext;

// Records whether the reader handed the payload to the client's metadata, which the corpus checks as
// `payloadDeserializationAttempted`: the converter wraps the registered metadata and notes each call.
internal sealed class DecodeRecorder
{
    public bool Attempted { get; private set; }

    public JsonTypeInfo<T> Wrap<T>(JsonTypeInfo<T> registered)
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = PayloadJsonContext.Default };
        return JsonMetadataServices.CreateValueInfo<T>(options, new RecordingConverter<T>(registered, this));
    }

    private sealed class RecordingConverter<T>(JsonTypeInfo<T> registered, DecodeRecorder recorder) : JsonConverter<T>
    {
        // Called for a JSON null payload too, so that decoding it counts as an attempt.
        public override bool HandleNull => true;

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            recorder.Attempted = true;
            return JsonSerializer.Deserialize(ref reader, registered);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, registered);
    }
}
