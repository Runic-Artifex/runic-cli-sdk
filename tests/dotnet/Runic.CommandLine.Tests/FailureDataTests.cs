using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Runic.CommandLine.Tests;

internal static class FailureDataTests
{
    private const string Identity = "tests.clone-recovery/1";
    internal const string ExactPath = "/tmp/Feature branch/目录/e\u0301\nfile\t\u001b.dll";
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("failure-data/exact-domain-identities-and-sanitized-ordinary-fields", ExactRoundTrip),
        new("failure-data/snapshot-is-immutable", ImmutableSnapshot),
        new("failure-data/identity-is-checked-before-deserialization", IdentityBeforeShape),
        new("failure-data/payload-byte-bound-is-exact-and-never-truncates", ByteBounds),
        new("failure-data/raw-unicode-and-whitespace-stay-bounded-when-rewritten", RawPayloadRewrite),
        new("failure-data/payload-depth-bound-and-duplicates-are-enforced", DepthAndDuplicates),
        new("failure-data/reader-rejects-malformed-extension", MalformedExtension),
        new("failure-data/ordinary-failure-wire-remains-unchanged", OrdinaryFailure),
        new("failure-data/zero-exit-and-success-category-are-refused", FailureInvariants),
        new("failure-data/generated-handler-and-localization-preserve-data", GeneratedAndLocalized),
    ];

    private static ValueTask ExactRoundTrip()
    {
        CommandFailureData data = Create(new RecoveryReport(ExactPath, "feature/tmp/修复", true));
        byte[] frame = Frame(data, new CommandFault("RCLI3000", "Failure in /tmp/private/file.dll",
            new Dictionary<string, string> { ["path"] = ExactPath }));
        using JsonDocument json = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        JsonElement root = json.RootElement;
        AssertEx.Equal(CliProtocol.Identity, root.GetProperty("protocol").GetString());
        AssertEx.Equal(false, root.GetProperty("success").GetBoolean());
        AssertEx.Equal(10, root.GetProperty("exitCode").GetInt32());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("payloadType").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("payload").ValueKind);
        AssertEx.Equal(ExactPath, root.GetProperty("fault").GetProperty("data").GetProperty("payload").GetProperty("retainedDirectory").GetString());
        AssertEx.True(!root.GetProperty("fault").GetProperty("message").GetString()!.Contains("/tmp/", StringComparison.Ordinal));
        AssertEx.True(!root.GetProperty("fault").GetProperty("details").GetRawText().Contains("Feature branch", StringComparison.Ordinal));
        AssertEx.True(!frame.Contains((byte)0x1b));
        CommandResponse<TestResult> response = Read(frame);
        AssertEx.True(response.FailureData!.TryGet(Identity, RecoveryJsonContext.Default.RecoveryReport, out RecoveryReport? recovery));
        AssertEx.Equal(new RecoveryReport(ExactPath, "feature/tmp/修复", true), recovery);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ImmutableSnapshot()
    {
        string[] paths = [ExactPath];
        CommandFailureData data = CommandFailureData.Create(Identity, paths, RecoveryJsonContext.Default.StringArray);
        paths[0] = "changed";
        AssertEx.True(data.TryGet(Identity, RecoveryJsonContext.Default.StringArray, out string[]? first));
        AssertEx.Equal(ExactPath, first![0]);
        first[0] = "changed again";
        AssertEx.True(data.TryGet(Identity, RecoveryJsonContext.Default.StringArray, out string[]? second));
        AssertEx.Equal(ExactPath, second![0]);
        return ValueTask.CompletedTask;
    }

    private static ValueTask IdentityBeforeShape()
    {
        CommandFailureData data = Read(ExtensionFrame("{\"type\":\"tests.future/2\",\"payload\":false}" )).FailureData!;
        AssertEx.True(!data.TryGet(Identity, RecoveryJsonContext.Default.RecoveryReport, out RecoveryReport? ignored));
        AssertEx.True(ignored is null);
        CommandProtocolException mismatch = AssertEx.Throws<CommandProtocolException>(() =>
            data.TryGet("tests.future/2", RecoveryJsonContext.Default.RecoveryReport, out _));
        AssertEx.Equal("failure-data-shape-mismatch", mismatch.Kind);
        AssertEx.True(!mismatch.Message.Contains("RecoveryReport", StringComparison.Ordinal));
        AssertEx.Throws<ArgumentException>(() => CommandFailureData.Create("invalid", "x", RecoveryJsonContext.Default.String));
        return ValueTask.CompletedTask;
    }

    private static ValueTask ByteBounds()
    {
        string exact = new('x', CommandFailureData.MaximumPayloadBytes - 2);
        CommandFailureData data = CommandFailureData.Create(Identity, exact, RecoveryJsonContext.Default.String);
        AssertEx.True(Read(Frame(data)).FailureData!.TryGet(Identity, RecoveryJsonContext.Default.String, out string? value));
        AssertEx.Equal(exact, value);
        CommandProtocolException tooLarge = AssertEx.Throws<CommandProtocolException>(() =>
            CommandFailureData.Create(Identity, exact + "x", RecoveryJsonContext.Default.String));
        AssertEx.Equal("failure-data-byte-limit", tooLarge.Kind);
        AssertKind(ExtensionFrame("{\"type\":\"" + Identity + "\",\"payload\":\"" + exact + "x\"}"), "failure-data-byte-limit");
        string escaped = string.Concat(Enumerable.Repeat("é\n😀", 3_000));
        CommandFailureData escapedData = CommandFailureData.Create(Identity, escaped, RecoveryJsonContext.Default.String);
        AssertEx.True(Read(Frame(escapedData)).FailureData!.TryGet(Identity, RecoveryJsonContext.Default.String, out string? escapedValue));
        AssertEx.Equal(escaped, escapedValue);
        return ValueTask.CompletedTask;
    }

    private static ValueTask DepthAndDuplicates()
    {
        string nested = new string('[', CommandFailureData.MaximumPayloadDepth) + "null" + new string(']', CommandFailureData.MaximumPayloadDepth);
        using JsonDocument valid = JsonDocument.Parse(nested);
        CommandFailureData data = CommandFailureData.Create(Identity, valid.RootElement, RecoveryJsonContext.Default.JsonElement);
        AssertEx.True(Read(Frame(data)).FailureData is not null);
        AssertKind(ExtensionFrame("{\"type\":\"" + Identity + "\",\"payload\":[" + nested + "]}"), "failure-data-depth-limit");
        foreach (string duplicate in new[] { "{\"path\":1,\"path\":2}", "{\"nested\":[{\"path\":1,\"path\":2}]}" })
        {
            AssertKind(ExtensionFrame("{\"type\":\"" + Identity + "\",\"payload\":" + duplicate + "}"), "duplicate-property");
            using JsonDocument document = JsonDocument.Parse(duplicate);
            CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
                CommandFailureData.Create(Identity, document.RootElement, RecoveryJsonContext.Default.JsonElement));
            AssertEx.Equal("duplicate-property", exception.Kind);
        }
        return ValueTask.CompletedTask;
    }

    private static ValueTask RawPayloadRewrite()
    {
        string rawUnicode = new('é', 22_000);
        CommandFailureData data = Read(ExtensionFrame("{\"type\":\"" + Identity + "\",\"payload\":\"" + rawUnicode + "\"}")).FailureData!;
        byte[] rewritten = Frame(data);
        AssertEx.True(rewritten.Length < CommandFailureData.MaximumPayloadBytes);
        AssertEx.True(Read(rewritten).FailureData!.TryGet(Identity, RecoveryJsonContext.Default.String, out string? value));
        AssertEx.Equal(rawUnicode, value);

        data = Read(ExtensionFrame("{\"type\":\"" + Identity + "\",\"payload\":{\n  \"path\" : \" spaced \\\"quote\\\" \\nvalue \" ,\r\n  \"nested\" : [ 1, 2 ]\n}}" )).FailureData!;
        rewritten = Frame(data);
        AssertEx.True(!rewritten.AsSpan(0, rewritten.Length - 1).Contains((byte)'\n') && !rewritten.AsSpan(0, rewritten.Length - 1).Contains((byte)'\r'));
        AssertEx.True(Read(rewritten).FailureData!.TryGet(Identity, RecoveryJsonContext.Default.JsonElement, out JsonElement payload));
        AssertEx.Equal(" spaced \"quote\" \nvalue ", payload.GetProperty("path").GetString());
        AssertEx.Equal(2, payload.GetProperty("nested")[1].GetInt32());
        return ValueTask.CompletedTask;
    }

    private static ValueTask MalformedExtension()
    {
        foreach (string invalid in new[] { "null", "false", "{}", "{\"type\":\"invalid\",\"payload\":{}}", "{\"type\":\"tests.data/1\",\"type\":\"tests.data/1\",\"payload\":{}}", "{\"type\":\"tests.data/1\"}" })
            AssertEx.Throws<CommandProtocolException>(() => Read(ExtensionFrame(invalid)));
        return ValueTask.CompletedTask;
    }

    private static ValueTask OrdinaryFailure()
    {
        byte[] frame = CommandJsonEnvelopeWriter.Serialize(CommandResponse.Failed<TestResult>("req", "clone", 10,
            new CommandFault("RCLI3000", "The clone failed.")), TestJsonContext.Default.TestResult);
        AssertEx.Equal("{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"clone\",\"success\":false,\"exitCode\":10,\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI3000\",\"message\":\"The clone failed.\",\"details\":{},\"retryable\":false},\"diagnostics\":[]}\n", Encoding.UTF8.GetString(frame));
        AssertEx.True(Read(frame).FailureData is null);
        return ValueTask.CompletedTask;
    }

    private static ValueTask FailureInvariants()
    {
        CommandFailureData data = Create(new RecoveryReport(ExactPath, "branch", false));
        var fault = new CommandFault("RCLI3000", "The clone failed.");
        AssertEx.Throws<ArgumentOutOfRangeException>(() => CommandOutcome.FailureWithData<TestResult>(CommandExitCategory.Success, fault, data));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => CommandOutcome.FailureWithData<TestResult>((CommandExitCategory)99, fault, data));
        AssertEx.Throws<ArgumentException>(() => CommandResponse.FailedWithData<TestResult>("req", "clone", 0, fault, data));
        return ValueTask.CompletedTask;
    }

    private static async ValueTask GeneratedAndLocalized()
    {
        var console = new MemoryCommandConsole();
        int code = await new CommandApp(Runic.CommandLine.Generated.GeneratedCommandCatalog.Create())
        {
            Console = console, HandleCancelKeyPress = false, TextResolver = new FailureTextResolver(),
        }.RunAsync(["typed-recovery", "--output=json"]);
        AssertEx.Equal(CommandExitCodes.Cancelled, code);
        CommandResponse<TestResult> response = Read(Encoding.UTF8.GetBytes(console.StandardOutput));
        AssertEx.Equal("The operation stopped.", response.Fault!.Message);
        AssertEx.Equal(string.Empty, console.StandardError);
        AssertEx.True(response.FailureData!.TryGet(Identity, RecoveryJsonContext.Default.RecoveryReport, out RecoveryReport? recovery));
        AssertEx.Equal(ExactPath, recovery!.RetainedDirectory);

        var human = new MemoryCommandConsole();
        await new CommandApp(Runic.CommandLine.Generated.GeneratedCommandCatalog.Create())
        { Console = human, HandleCancelKeyPress = false }.RunAsync(["typed-recovery"]);
        AssertEx.Equal("Inspect the retained directory.\n", human.StandardOutput);
        AssertEx.True(!human.StandardError.Contains(ExactPath, StringComparison.Ordinal));
    }

    [Command("typed-recovery")]
    [CommandResult("tests.result/1", typeof(TestJsonContext))]
    internal static CommandOutcome<TestResult> TypedRecovery() => CommandOutcome.FailureWithData<TestResult>(
        CommandExitCategory.Cancelled, new CommandFault("RCLI3000", "The clone failed."),
        Create(new RecoveryReport(ExactPath, "feature/tmp/修复", true)), humanOutput: "Inspect the retained directory.\n");

    private static CommandFailureData Create(RecoveryReport report) =>
        CommandFailureData.Create(Identity, report, RecoveryJsonContext.Default.RecoveryReport);
    private static CommandResponse<TestResult> Read(byte[] frame) => CommandJsonEnvelopeReader.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult);
    private static byte[] Frame(CommandFailureData data, CommandFault? fault = null) => CommandJsonEnvelopeWriter.Serialize(
        CommandResponse.FailedWithData<TestResult>("req", "clone", 10, fault ?? new CommandFault("RCLI3000", "The clone failed."), data), TestJsonContext.Default.TestResult);
    private static byte[] ExtensionFrame(string data) => Encoding.UTF8.GetBytes(
        "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"clone\",\"success\":false,\"exitCode\":10,\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI3000\",\"message\":\"The clone failed.\",\"details\":{},\"retryable\":false,\"data\":" + data + "},\"diagnostics\":[]}\n");
    private static void AssertKind(byte[] frame, string kind) => AssertEx.Equal(kind, AssertEx.Throws<CommandProtocolException>(() => Read(frame)).Kind);

    private sealed class FailureTextResolver : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) => key == "faults.RCLI3000" ? "The operation stopped." : null;
    }
}

internal sealed record RecoveryReport(string RetainedDirectory, string TargetBranch, bool ObservationComplete);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RecoveryReport))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class RecoveryJsonContext : JsonSerializerContext;
