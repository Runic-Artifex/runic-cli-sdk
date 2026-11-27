using System.Text.Json;
using System.Text.Json.Serialization;

namespace Runic.CommandLine.Tests;

internal sealed record GrammarCorpus(
    int FormatVersion,
    string Protocol,
    CorpusCatalog Catalog,
    CorpusCase[] Cases);

internal sealed record CorpusCatalog(CorpusCommand[] Commands, string? JsonOutputAlias = null, CorpusOption[]? GlobalOptions = null);

internal sealed record CorpusCommand(
    string[] Path,
    string[] Aliases,
    CorpusOption[] Options,
    CorpusArgument[] Arguments,
    bool Group = false);

internal sealed record CorpusOption(
    string Id,
    [property: JsonPropertyName("long")] string LongName,
    string[] Aliases,
    CorpusArity Arity,
    string Repeat,
    bool Required = false,
    string[]? Choices = null,
    string? EnvironmentVariable = null,
    bool AcceptsNegativeNumbers = false);

internal sealed record CorpusArgument(string Id, CorpusArity Arity, bool AcceptsNegativeNumbers = false);

internal sealed record CorpusArity(JsonElement Minimum, JsonElement Maximum);

internal sealed record CorpusCase(string Id, string[] Args, CorpusExpected Expected, Dictionary<string, string>? Environment = null);

internal sealed record CorpusExpected(
    string Kind,
    string[]? CommandPath,
    CorpusValue[]? Options,
    CorpusValue[]? Arguments,
    CorpusDiagnostic[]? Diagnostics);

internal sealed record CorpusValue(string Id, string[] Values);

internal sealed record CorpusDiagnostic(string Code, string Kind, int TokenIndex, string[]? Arguments = null, string? Binding = null);

internal sealed record OutputClassificationCorpus(
    int FormatVersion,
    string Protocol,
    string EnvironmentVariable,
    OutputClassificationCase[] Cases);

internal sealed record OutputClassificationCase(
    string Id,
    string[] Args,
    Dictionary<string, string?> Environment,
    OutputClassificationExpected Expected);

internal sealed record OutputClassificationExpected(
    string Kind,
    string? Mode,
    string? Source,
    CorpusDiagnostic[]? Diagnostics);

internal sealed record ProtocolManifest(
    int FormatVersion,
    string Protocol,
    string OutputEnvironmentVariable,
    string ImplementationNamespace,
    string Encoding,
    string Framing,
    ProtocolLimits Limits,
    string[] EscapingProfiles,
    string[] ReaderReasons,
    Dictionary<string, SamplePayloadSchema> SamplePayloads,
    ProtocolFixture[] Fixtures);

// The JSON shape a sample payload type decodes from (protocol README, "Sample payload types").
internal sealed record SamplePayloadSchema(string JsonKind, bool Nullable, Dictionary<string, string> Members);

internal sealed record ProtocolLimits(
    int FramedBytes,
    int JsonDepth,
    int RequestIdBytes,
    int CommandBytes,
    int PayloadTypeBytes,
    int CodeBytes,
    int MessageBytes,
    int DiagnosticCount,
    int FaultDetailCount,
    int FailureDataPayloadBytes,
    int FailureDataPayloadDepth,
    int DiagnosticArgumentCount,
    int DetailKeyBytes,
    int DetailValueBytes,
    int DiagnosticKindBytes,
    int DiagnosticCommandPathBytes,
    int DiagnosticMessageKeyBytes,
    int DiagnosticArgumentBytes,
    int DiagnosticCommandPathSegments,
    int HelpUriCharacters);

internal sealed record ProtocolFixture(string Id, string Class, string Path, string? Outcome, int? CaseCount);

internal sealed record WireCorpus(
    int FormatVersion,
    string Protocol,
    string Representation,
    ProtocolClientContext ClientContext,
    WireCase[] Cases);

internal sealed record WireCase(
    string Id,
    string? WireUtf8Base64,
    WireRecipe? WireRecipe,
    int? ExpectedWireBytes,
    ProtocolExpected Expected);

internal sealed record WireRecipe(string Kind, string Prefix, string Value, int Count, string Suffix);

internal sealed record SemanticProtocolCorpus(int FormatVersion, string Protocol, SemanticProtocolCase[] Cases);

internal sealed record SemanticProtocolCase(
    string Id,
    JsonElement Document,
    ProtocolClientContext? ClientContext,
    ProtocolExpected Expected);

internal sealed record ProtocolClientContext(string[]? SupportedPayloadTypes, string? ExpectedPayloadJsonKind);

internal sealed record ProtocolExpected(
    string Kind,
    string Reason,
    bool? PayloadDeserializationAttempted);

// sample.status/1 and sample.optional-status/1 as the manifest declares them: only `state`, so any other member is
// unknown, as in invalid.unknown-field-is-accepted.
internal sealed record StatusPayload(string? State);

internal sealed record WriterCorpus(int FormatVersion, string Protocol, WriterCase[] Cases);

internal sealed record WriterCase(string Id, WriterInput Input, WriterExpected Expected);

internal sealed record WriterInput(
    string RequestId,
    string Command,
    bool Success,
    int ExitCode,
    string? PayloadType,
    JsonElement Payload,
    WriterFault? Fault,
    WriterFailureData? FailureData,
    WriterDiagnostic[] Diagnostics);

internal sealed record WriterFault(string Code, string Message, string[][] Details, bool Retryable, string? HelpUri);

internal sealed record WriterFailureData(string Type, JsonElement Payload);

internal sealed record WriterDiagnostic(
    string Code,
    string Kind,
    string[] CommandPath,
    string MessageKey,
    string Message,
    string Phase,
    string Severity,
    int? TokenIndex,
    string[] Arguments,
    string? Binding,
    string? HelpUri);

internal sealed record WriterExpected(string? Example, JsonElement? Document);

internal sealed record ExportPayload(int Written, string OutputPath);

// The doubles of sample.measurement/1, in the order of examples/number-text.json.
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
[JsonSerializable(typeof(GrammarCorpus))]
[JsonSerializable(typeof(OutputClassificationCorpus))]
[JsonSerializable(typeof(ProtocolManifest))]
[JsonSerializable(typeof(WireCorpus))]
[JsonSerializable(typeof(SemanticProtocolCorpus))]
[JsonSerializable(typeof(WriterCorpus))]
[JsonSerializable(typeof(StatusPayload))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ExportPayload))]
[JsonSerializable(typeof(MeasurementPayload))]
internal sealed partial class CorpusJsonContext : JsonSerializerContext;
