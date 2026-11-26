using System.Text;
using System.Text.Json;

namespace Runic.CommandLine.Tests;

internal sealed class ProtocolCorpusTests
{
    private static readonly string ProtocolRoot = Path.Combine(AppContext.BaseDirectory, "Corpus", "protocol");
    private static readonly ProtocolManifest Manifest = Read("manifest.json", CorpusJsonContext.Default.ProtocolManifest);
    private static readonly WireCorpus Wire = Read("wire-inputs.json", CorpusJsonContext.Default.WireCorpus);
    private static readonly SemanticProtocolCorpus Semantic = Read(
        "invalid-structures.json",
        CorpusJsonContext.Default.SemanticProtocolCorpus);
    private static readonly WriterCorpus Writer = Read("writer-cases.json", CorpusJsonContext.Default.WriterCorpus);

    public static IEnumerable<string> WireCaseIds()
    {
        AssertEx.Equal(41, Wire.Cases.Length, "The frozen wire corpus row count changed.");
        return Wire.Cases.Select(static test => test.Id);
    }

    public static IEnumerable<string> SemanticCaseIds()
    {
        AssertEx.Equal(80, Semantic.Cases.Length, "The frozen semantic protocol corpus row count changed.");
        return Semantic.Cases.Select(static test => test.Id);
    }

    public static IEnumerable<string> WriterCaseIds()
    {
        AssertEx.Equal(19, Writer.Cases.Length, "The frozen writer corpus row count changed.");
        return Writer.Cases.Select(static test => test.Id);
    }

    // The examples come from the manifest, so a new example needs no change here.
    public static IEnumerable<string> Examples() =>
        Manifest.Fixtures.Where(static fixture => fixture.Class == "valid")
            .Select(static fixture => Path.GetFileNameWithoutExtension(fixture.Path));

    [Test, MethodDataSource(nameof(WireCaseIds)), CorpusCaseName("protocol/")]
    public ValueTask WireInput(string id) => WireCase(Wire.Cases.Single(test => test.Id == id));

    [Test, MethodDataSource(nameof(SemanticCaseIds)), CorpusCaseName("protocol/")]
    public ValueTask SemanticStructure(string id) => SemanticCase(Semantic.Cases.Single(test => test.Id == id));

    [Test, MethodDataSource(nameof(WriterCaseIds)), CorpusCaseName("protocol/")]
    public ValueTask WriterOutput(string id) => WriterCase(Writer.Cases.Single(test => test.Id == id));

    [Test, MethodDataSource(nameof(Examples)), CorpusCaseName("protocol/example.")]
    public ValueTask ExampleFrame(string name) => Example(name);

    [Test, DisplayName("protocol/manifest-identities-and-limits")]
    public ValueTask ManifestIdentitiesAndLimits()
    {
        ProtocolManifest manifest = Manifest;
        AssertEx.Equal(1, manifest.FormatVersion);
        AssertEx.Equal(CommandProtocol.Identity, manifest.Protocol);
        AssertEx.Equal(CommandOutputClassifier.EnvironmentVariableName, manifest.OutputEnvironmentVariable);
        AssertEx.Equal("Runic.CommandLine", manifest.ImplementationNamespace);
        AssertEx.Equal("utf-8", manifest.Encoding);
        AssertEx.Equal("one-json-object-one-lf", manifest.Framing);
        AssertEx.Equal(CommandJsonEnvelopeReader.DefaultMaximumFrameBytes, manifest.Limits.FramedBytes);
        AssertEx.Equal(32, manifest.Limits.JsonDepth);
        AssertEx.Equal(128, manifest.Limits.RequestIdBytes);
        AssertEx.Equal(512, manifest.Limits.CommandBytes);
        AssertEx.Equal(128, manifest.Limits.PayloadTypeBytes);
        AssertEx.Equal(64, manifest.Limits.DiagnosticCount);
        AssertEx.Equal(CommandFailureData.MaximumPayloadBytes, manifest.Limits.FailureDataPayloadBytes);
        AssertEx.Equal(CommandFailureData.MaximumPayloadDepth, manifest.Limits.FailureDataPayloadDepth);
        AssertEx.Equal(64, manifest.Limits.DiagnosticCommandPathSegments);
        AssertEx.Equal(2_048, manifest.Limits.HelpUriCharacters);
        AssertEx.SequenceEqual(["dotnet"], manifest.EscapingProfiles);
        AssertEx.Equal(Wire.Cases.Length, manifest.Fixtures.Single(static fixture => fixture.Id == "protocol.wire-inputs").CaseCount);
        AssertEx.Equal(Semantic.Cases.Length, manifest.Fixtures.Single(static fixture => fixture.Id == "protocol.invalid-structures").CaseCount);
        AssertEx.Equal(Writer.Cases.Length, manifest.Fixtures.Single(static fixture => fixture.Id == "protocol.writer-cases").CaseCount);
        return ValueTask.CompletedTask;
    }

    // Every rejection the corpora expect is a listed reader reason, every listed reason is listed once, and every
    // example has the writer case that produces it.
    [Test, DisplayName("protocol/manifest-reasons-and-writer-coverage")]
    public ValueTask ManifestReasonsAndWriterCoverage()
    {
        AssertEx.Equal(Manifest.ReaderReasons.Length, Manifest.ReaderReasons.Distinct(StringComparer.Ordinal).Count());
        IEnumerable<ProtocolExpected> rejections = Wire.Cases.Select(static test => test.Expected)
            .Concat(Semantic.Cases.Select(static test => test.Expected))
            .Where(static expected => expected.Kind == "protocol-error");
        foreach (ProtocolExpected expected in rejections)
        {
            AssertEx.True(Manifest.ReaderReasons.Contains(expected.Reason), $"Reason '{expected.Reason}' is not in the manifest.");
        }

        foreach (ProtocolFixture example in Manifest.Fixtures.Where(static fixture => fixture.Class == "valid"))
        {
            AssertEx.True(
                Writer.Cases.Any(test => test.Expected.Example == example.Path),
                $"Example '{example.Path}' has no writer case.");
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask WireCase(WireCase test)
    {
        byte[] frame = test.WireRecipe is null
            ? Convert.FromBase64String(test.WireUtf8Base64!)
            : CreateWire(test.WireRecipe);
        if (test.ExpectedWireBytes is int expectedBytes)
        {
            AssertEx.Equal(expectedBytes, frame.Length, test.Id);
        }

        string expectedPayloadType = Wire.ClientContext.SupportedPayloadTypes!.Single();
        if (test.Expected.Kind == "accepted")
        {
            _ = CommandJsonEnvelopeReader.Read(frame, expectedPayloadType, CorpusJsonContext.Default.StatusPayload);
            return ValueTask.CompletedTask;
        }

        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(
                frame,
                expectedPayloadType,
                CorpusJsonContext.Default.StatusPayload));
        AssertEx.Equal(
            test.Expected.Reason,
            exception.Kind,
            $"{test.Id}: expected {test.Expected.Reason}, found {exception.Kind}.");
        return ValueTask.CompletedTask;
    }

    private static ValueTask SemanticCase(SemanticProtocolCase test)
    {
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(test.Document);
        byte[] frame = new byte[document.Length + 1];
        document.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        // Every case names its client's one supported payload type (protocol README, "Client context").
        AssertEx.True(test.ClientContext?.SupportedPayloadTypes?.Length == 1, $"{test.Id} needs one supported payload type.");
        string expectedPayloadType = test.ClientContext!.SupportedPayloadTypes![0];

        if (test.Expected.Kind == "accepted")
        {
            CommandResponse<StatusPayload> response = CommandJsonEnvelopeReader.Read(
                frame,
                expectedPayloadType,
                CorpusJsonContext.Default.StatusPayload);
            AssertEx.Equal(test.Document.GetProperty("success").GetBoolean(), response.Succeeded, test.Id);
            AssertEx.Equal(test.Document.GetProperty("exitCode").GetInt32(), response.ExitCode, test.Id);
            AssertEx.Equal(test.Document.GetProperty("requestId").GetString(), response.RequestId, test.Id);
            AssertEx.Equal(test.Document.GetProperty("command").GetString(), response.Command, test.Id);
            AssertEx.Equal(test.Document.GetProperty("diagnostics").GetArrayLength(), response.Diagnostics.Count, test.Id);
        }
        else
        {
            CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
                CommandJsonEnvelopeReader.Read(
                    frame,
                    expectedPayloadType,
                    CorpusJsonContext.Default.StatusPayload));
            AssertEx.Equal(test.Expected.Reason, exception.Kind, test.Id);
        }

        return ValueTask.CompletedTask;
    }

    // A writer case passes when the output is one framed JSON object whose value equals the expected one. An
    // example is also the exact output of the dotnet escaping profile, which this writer implements, so its bytes
    // must match as well (protocol README, "Examples and escaping profiles").
    private static ValueTask WriterCase(WriterCase test)
    {
        byte[] actual = Serialize(test.Input);
        AssertEx.True(actual.Length > 2 && actual[0] == (byte)'{' && actual[^1] == (byte)'\n' && actual[^2] == (byte)'}', test.Id);
        using JsonDocument actualDocument = JsonDocument.Parse(actual.AsMemory(0, actual.Length - 1));
        if (test.Expected.Example is { } example)
        {
            byte[] expected = File.ReadAllBytes(Path.Combine(ProtocolRoot, example));
            using JsonDocument expectedDocument = JsonDocument.Parse(expected);
            AssertEx.True(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement), $"{test.Id}: the value differs from {example}.");
            AssertEx.SequenceEqual(expected, actual, $"{test.Id}: the dotnet profile output differs from {example}.");
        }
        else
        {
            JsonElement expected = test.Expected.Document ?? throw new InvalidOperationException($"{test.Id} has no expected output.");
            AssertEx.True(
                JsonElement.DeepEquals(expected, actualDocument.RootElement),
                $"{test.Id}: expected {expected.GetRawText()}, found {Encoding.UTF8.GetString(actual)}");
        }

        return ValueTask.CompletedTask;
    }

    private static byte[] Serialize(WriterInput input)
    {
        IReadOnlyList<CommandDiagnostic> diagnostics = input.Diagnostics.Select(Diagnostic).ToArray();
        if (input.Success)
        {
            CommandResponse<JsonElement> success = input.ExitCode == 0
                ? CommandResponse.Success(input.RequestId, input.Command, input.PayloadType!, input.Payload, diagnostics)
                : CommandResponse.FromOutcome(
                    input.RequestId,
                    input.Command,
                    input.ExitCode,
                    input.PayloadType!,
                    CommandOutcome.Success(input.Payload).WithExitCode(input.ExitCode),
                    diagnostics);
            return CommandJsonEnvelopeWriter.Serialize(success, CorpusJsonContext.Default.JsonElement);
        }

        WriterFault fault = input.Fault!;
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string[] detail in fault.Details)
        {
            details.Add(detail[0], detail[1]);
        }

        CommandFailureData? failureData = input.FailureData is { } data
            ? CommandFailureData.Create(data.Type, data.Payload, CorpusJsonContext.Default.JsonElement)
            : null;
        CommandResponse<JsonElement> failure = CommandResponse.Failure<JsonElement>(
            input.RequestId,
            input.Command,
            input.ExitCode,
            new CommandFault(fault.Code, fault.Message, details, fault.Retryable) { HelpUri = Uri(fault.HelpUri) },
            diagnostics,
            failureData);
        return CommandJsonEnvelopeWriter.Serialize(failure, CorpusJsonContext.Default.JsonElement);
    }

    private static CommandDiagnostic Diagnostic(WriterDiagnostic input) =>
        new(
            input.Code,
            input.Kind,
            input.Message,
            input.Phase switch
            {
                "parse" => CommandDiagnosticPhase.Parse,
                "binding" => CommandDiagnosticPhase.Binding,
                "execution" => CommandDiagnosticPhase.Execution,
                _ => throw new InvalidOperationException($"Unknown diagnostic phase '{input.Phase}'."),
            },
            input.Severity switch
            {
                "error" => CommandDiagnosticSeverity.Error,
                "warning" => CommandDiagnosticSeverity.Warning,
                "information" => CommandDiagnosticSeverity.Information,
                _ => throw new InvalidOperationException($"Unknown diagnostic severity '{input.Severity}'."),
            },
            input.TokenIndex,
            input.Arguments,
            input.CommandPath.Length == 0 ? CommandPath.Root : new CommandPath(input.CommandPath),
            input.MessageKey)
        {
            Binding = input.Binding,
            HelpUri = Uri(input.HelpUri),
        };

    private static Uri? Uri(string? value) => value is null ? null : new Uri(value, UriKind.Absolute);

    // Every example is a valid frame that the strict reader accepts.
    private static ValueTask Example(string name)
    {
        byte[] expected = File.ReadAllBytes(Path.Combine(ProtocolRoot, "examples", $"{name}.json"));
        AssertEx.True(expected.Length > 2 && expected[^1] == (byte)'\n', $"Example {name} is not LF framed.");
        using JsonDocument document = JsonDocument.Parse(expected);
        bool success = document.RootElement.GetProperty("success").GetBoolean();
        CommandResponse<ExportPayload> roundTrip = CommandJsonEnvelopeReader.Read(
            expected,
            success ? document.RootElement.GetProperty("payloadType").GetString()! : "sample.unused/1",
            CorpusJsonContext.Default.ExportPayload);
        AssertEx.Equal(success, roundTrip.Succeeded);
        AssertEx.Equal(document.RootElement.GetProperty("exitCode").GetInt32(), roundTrip.ExitCode);
        AssertEx.Equal(name == "missing-argument" ? "destination" : null, roundTrip.Diagnostics.SingleOrDefault()?.Binding);
        return ValueTask.CompletedTask;
    }

    private static byte[] CreateWire(WireRecipe recipe)
    {
        AssertEx.Equal("repeat-utf8", recipe.Kind);
        string wire = string.Concat(recipe.Prefix, string.Concat(Enumerable.Repeat(recipe.Value, recipe.Count)), recipe.Suffix);
        return Encoding.UTF8.GetBytes(wire);
    }

    private static T Read<T>(string relativePath, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(ProtocolRoot, relativePath)), typeInfo)
        ?? throw new InvalidOperationException($"Protocol corpus '{relativePath}' could not be read.");
}
