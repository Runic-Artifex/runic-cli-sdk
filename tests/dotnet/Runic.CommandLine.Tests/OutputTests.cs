using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Runic.CommandLine.Tests;

internal static class OutputTests
{
    private static readonly string[] EnvelopeProperties =
        ["protocol", "requestId", "command", "success", "exitCode", "payloadType", "payload", "fault", "diagnostics"];
    private static readonly string[] OrderedDetailKeys = ["Alpha", "alpha", "zulu"];
    private static readonly string[] DiagnosticProperties =
        ["code", "kind", "commandPath", "messageKey", "message", "phase", "severity", "tokenIndex", "arguments"];

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("output/json-frame-is-one-pure-utf8-object", JsonFramePurity),
        new("output/json-property-order-is-deterministic", DeterministicPropertyOrder),
        new("output/faults-and-diagnostics-are-sanitized", Sanitization),
        new("output/unsafe-fault-message-keeps-well-formed-code", UnsafeMessageKeepsWellFormedCode),
        new("output/invalid-fault-code-collapses-to-rcli5000", InvalidCodeCollapsesToSoftwareFailure),
        new("output/fault-details-use-ordinal-order", DetailOrdering),
        new("output/source-generated-payload-round-trips", SourceGeneratedRoundTrip),
        new("output/reader-accepts-unknown-additive-members", ReaderAcceptsAdditiveMembers),
        new("output/reader-rejects-impure-framing-and-invalid-utf8", ReaderRejectsImpureFrames),
        new("output/reader-rejects-duplicate-and-oversized-envelopes", ReaderRejectsDuplicateAndOversized),
        new("output/reader-checks-payload-identity-before-deserialization", ReaderChecksIdentityFirst),
        new("output/reader-rejects-unsafe-fault-content", ReaderRejectsUnsafeFault),
        new("output/writer-rejects-payload-over-one-mebibyte", WriterRejectsOversizedPayload),
        new("output/dispatcher-keeps-json-off-stderr", DispatcherJsonChannels),
        new("output/dispatcher-keeps-human-fault-off-stdout", DispatcherHumanChannels),
        new("output/dispatcher-human-unsafe-fault-keeps-code", DispatcherHumanUnsafeFaultKeepsCode),
        new("output/dispatcher-writes-success-diagnostics-to-human-stderr", DispatcherHumanSuccessDiagnostics),
        new("output/dispatcher-does-not-repeat-a-fault-diagnostic", DispatcherDoesNotRepeatFaultDiagnostic),
    ];

    private static ValueTask JsonFramePurity()
    {
        byte[] frame = SuccessFrame();
        AssertEx.True(frame.Length > 2);
        AssertEx.Equal((byte)'{', frame[0]);
        AssertEx.Equal((byte)'\n', frame[^1]);
        AssertEx.True(frame[..^1].All(static value => value != (byte)'\n' && value != (byte)'\r'));
        AssertEx.True(!frame.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        AssertEx.True(!frame.Contains((byte)0x1B), "Machine output contained an ANSI escape byte.");
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        AssertEx.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        return ValueTask.CompletedTask;
    }

    private static ValueTask DeterministicPropertyOrder()
    {
        byte[] first = SuccessFrame();
        byte[] second = SuccessFrame();
        AssertEx.SequenceEqual(first, second);
        using JsonDocument document = JsonDocument.Parse(first.AsMemory(0, first.Length - 1));
        AssertEx.SequenceEqual(
            EnvelopeProperties,
            document.RootElement.EnumerateObject().Select(static property => property.Name));
        return ValueTask.CompletedTask;
    }

    private static ValueTask Sanitization()
    {
        var fault = new CommandFault(
            "RCLI3000",
            "unsafe\u001b[31m\r\nmessage",
            new Dictionary<string, string> { ["line"] = "one\r\ntwo" });
        var diagnostic = new CommandDiagnostic(
            "RCLI2001",
            "invalid-value",
            "bad\u001b[31m\nvalue",
            CommandDiagnosticPhase.Binding,
            CommandDiagnosticSeverity.Error,
            2);
        byte[] frame = CommandJsonEnvelopeWriter.Serialize(
            CommandResponse.Failed<TestResult>("req-2", "status", 10, fault, [diagnostic]),
            TestJsonContext.Default.TestResult);
        string json = Encoding.UTF8.GetString(frame);

        AssertEx.True(!json.Contains('\u001b'));
        AssertEx.True(!json.Contains('\r'));
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        AssertEx.Equal("unsafe [31m message", document.RootElement.GetProperty("fault").GetProperty("message").GetString());
        AssertEx.Equal("one two", document.RootElement.GetProperty("fault").GetProperty("details").GetProperty("line").GetString());
        AssertEx.Equal("bad [31m value", document.RootElement.GetProperty("diagnostics")[0].GetProperty("message").GetString());
        AssertEx.SequenceEqual(
            DiagnosticProperties,
            document.RootElement.GetProperty("diagnostics")[0]
                .EnumerateObject().Select(static property => property.Name));
        return ValueTask.CompletedTask;
    }

    private static ValueTask DetailOrdering()
    {
        var fault = new CommandFault(
            "RCLI3000",
            "failure",
            new Dictionary<string, string>
            {
                ["zulu"] = "last",
                ["Alpha"] = "first",
                ["alpha"] = "middle",
            });
        byte[] frame = CommandJsonEnvelopeWriter.Serialize(
            CommandResponse.Failed<TestResult>("req-3", "status", 10, fault),
            TestJsonContext.Default.TestResult);
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        AssertEx.SequenceEqual(
            OrderedDetailKeys,
            document.RootElement.GetProperty("fault").GetProperty("details")
                .EnumerateObject().Select(static property => property.Name));
        return ValueTask.CompletedTask;
    }

    private static ValueTask SourceGeneratedRoundTrip()
    {
        byte[] frame = SuccessFrame();
        CommandResponse<TestResult> response = CommandJsonEnvelopeReader.Read(
            frame,
            TestCodec.Identity,
            TestJsonContext.Default.TestResult);
        AssertEx.True(response.Success);
        AssertEx.Equal(new TestResult(42, "ok"), response.Payload);

        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(frame, "different/1", TestJsonContext.Default.TestResult));
        AssertEx.Equal("unsupported-payload-type", exception.Kind);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ReaderAcceptsAdditiveMembers()
    {
        byte[] original = SuccessFrame();
        string json = Encoding.UTF8.GetString(original.AsSpan(0, original.Length - 2));
        byte[] extended = Encoding.UTF8.GetBytes($"{json},\"future\":{{\"nested\":true}}}}\n");
        CommandResponse<TestResult> response = CommandJsonEnvelopeReader.Read(
            extended,
            TestCodec.Identity,
            TestJsonContext.Default.TestResult);
        AssertEx.Equal(new TestResult(42, "ok"), response.Payload);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ReaderRejectsImpureFrames()
    {
        byte[] valid = SuccessFrame();
        var frames = new List<byte[]>
        {
            Encoding.UTF8.GetPreamble().Concat(valid).ToArray(),
            valid.Prepend((byte)' ').ToArray(),
            valid[..^1].Append((byte)'\r').Append((byte)'\n').ToArray(),
            valid.Append((byte)'\n').ToArray(),
        };
        byte[] invalidUtf8 = valid.ToArray();
        invalidUtf8[2] = 0xFF;
        frames.Add(invalidUtf8);

        string[] expectedKinds = ["utf8-bom", "invalid-framing", "invalid-framing", "invalid-framing", "invalid-utf8"];
        for (int index = 0; index < frames.Count; index++)
        {
            AssertProtocolKind(frames[index], expectedKinds[index]);
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask ReaderRejectsDuplicateAndOversized()
    {
        byte[] original = SuccessFrame();
        string json = Encoding.UTF8.GetString(original.AsSpan(0, original.Length - 2));
        byte[] duplicate = Encoding.UTF8.GetBytes($"{json},\"protocol\":\"{CliProtocol.Identity}\"}}\n");
        AssertProtocolKind(duplicate, "duplicate-property");

        CommandProtocolException oversized = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(
                original,
                TestCodec.Identity,
                TestJsonContext.Default.TestResult,
                original.Length - 1));
        AssertEx.Equal("framed-byte-limit", oversized.Kind);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ReaderChecksIdentityFirst()
    {
        byte[] original = SuccessFrame();
        string json = Encoding.UTF8.GetString(original);
        int payloadStart = json.IndexOf("\"payload\":", StringComparison.Ordinal);
        int faultStart = json.IndexOf(",\"fault\":", payloadStart, StringComparison.Ordinal);
        string hostile = string.Concat(json.AsSpan(0, payloadStart), "\"payload\":false", json.AsSpan(faultStart));
        byte[] frame = Encoding.UTF8.GetBytes(hostile);

        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(frame, "different/1", TestJsonContext.Default.TestResult));
        AssertEx.Equal("unsupported-payload-type", exception.Kind);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ReaderRejectsUnsafeFault()
    {
        const string Unsafe = "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"status\",\"success\":false,\"exitCode\":10,\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI3000\",\"message\":\"bad\\u001btext\",\"details\":{},\"retryable\":false},\"diagnostics\":[]}\n";
        AssertProtocolKind(Encoding.UTF8.GetBytes(Unsafe), "unsafe-fault-content");
        return ValueTask.CompletedTask;
    }

    private static ValueTask WriterRejectsOversizedPayload()
    {
        var response = CommandResponse.Succeeded(
            "req-large",
            "status",
            TestCodec.Identity,
            new TestResult(1, new string('x', CommandJsonEnvelopeReader.DefaultMaximumFrameBytes)));
        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeWriter.Serialize(response, TestJsonContext.Default.TestResult));
        AssertEx.Equal("frame-too-large", exception.Kind);
        return ValueTask.CompletedTask;
    }

    private static void AssertProtocolKind(byte[] frame, string expectedKind)
    {
        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult));
        AssertEx.Equal(expectedKind, exception.Kind);
    }

    private static async ValueTask DispatcherJsonChannels()
    {
        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Json,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Succeeded(
                "req-json", "status", TestCodec.Identity, new TestResult(42, "ok")),
            new TestCodec());
        AssertEx.True(console.StandardOutput.StartsWith('{'));
        AssertEx.True(console.StandardOutput.EndsWith('\n'));
        AssertEx.Equal(string.Empty, console.StandardError);
    }

    private static async ValueTask DispatcherHumanChannels()
    {
        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Failed<TestResult>(
                "req-human",
                "status",
                10,
                new CommandFault("RCLI3000", "Expected failure.")),
            new TestCodec());
        AssertEx.Equal(string.Empty, console.StandardOutput);
        AssertEx.Equal("RCLI3000: Expected failure.\n", console.StandardError);
    }

    private static async ValueTask DispatcherHumanSuccessDiagnostics()
    {
        var console = new MemoryCommandConsole();
        var diagnostic = new CommandDiagnostic(
            "RCLI3001", "deprecated-option", "The option is deprecated.",
            CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning);
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Succeeded(
                "req-human-warning", "status", TestCodec.Identity, new TestResult(7, "ok"), [diagnostic]),
            new TestCodec());

        AssertEx.Equal("7:ok\n", console.StandardOutput);
        AssertEx.Equal("RCLI3001: The option is deprecated.\n", console.StandardError);
    }

    private static async ValueTask DispatcherDoesNotRepeatFaultDiagnostic()
    {
        var console = new MemoryCommandConsole();
        var fault = new CommandFault("RCLI3000", "Expected failure.");
        var diagnostic = new CommandDiagnostic(
            "RCLI3000", "command-failed", "Expected failure.",
            CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Error);
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Failed<TestResult>("req-human-fault", "status", 10, fault, [diagnostic]),
            new TestCodec());

        AssertEx.Equal("RCLI3000: Expected failure.\n", console.StandardError);
    }

    private static ValueTask UnsafeMessageKeepsWellFormedCode()
    {
        foreach (string code in new[] { "RCLI3001", "RCLI8042", "RAS1001", "ACME.IO_FAILURE-2" })
        {
            var fault = new CommandFault(
                code,
                "Could not read /home/ada/project/assets/atlas.png.",
                new Dictionary<string, string>
                {
                    ["path"] = "/home/ada/project/assets/atlas.png",
                    ["reason"] = "missing-file",
                },
                retryable: true);
            CommandResponse<TestResult> response =
                CommandResponse.Failed<TestResult>("req-keep", "pack", 10, fault);
            AssertEx.Equal(10, response.ExitCode);
            AssertEx.Equal(code, response.Fault!.Code);

            byte[] frame = CommandJsonEnvelopeWriter.Serialize(response, TestJsonContext.Default.TestResult);
            string json = Encoding.UTF8.GetString(frame);
            AssertEx.True(!json.Contains("/home/", StringComparison.Ordinal), "The unsafe path leaked into JSON.");
            using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
            AssertEx.Equal(10, document.RootElement.GetProperty("exitCode").GetInt32());
            JsonElement written = document.RootElement.GetProperty("fault");
            AssertEx.Equal(code, written.GetProperty("code").GetString());
            AssertEx.Equal(
                "The command failed; details were redacted.",
                written.GetProperty("message").GetString());
            AssertEx.Equal("[redacted]", written.GetProperty("details").GetProperty("path").GetString());
            AssertEx.Equal("missing-file", written.GetProperty("details").GetProperty("reason").GetString());
            AssertEx.True(written.GetProperty("retryable").GetBoolean());

            CommandResponse<TestResult> roundTrip = CommandJsonEnvelopeReader.Read(
                frame, TestCodec.Identity, TestJsonContext.Default.TestResult);
            AssertEx.Equal(code, roundTrip.Fault!.Code);
        }

        foreach (string message in new[] { "System.IO.IOException: denied", @"C:\Users\ada\atlas.png", @"\\server\share", "/tmp/runic/x" })
        {
            CommandFault sanitized = CommandResponse.Failed<TestResult>(
                "req-keep", "pack", 10, new CommandFault("RAS1001", message)).Fault!;
            AssertEx.Equal("RAS1001", sanitized.Code);
            AssertEx.Equal("The command failed; details were redacted.", sanitized.Message);
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask InvalidCodeCollapsesToSoftwareFailure()
    {
        foreach (string code in new[] { "ras1001", "1001", "RAS 1001", "RAS/1001", "RAS\u001b1001", new string('A', 65) })
        {
            var fault = new CommandFault(
                code,
                "A safe message.",
                new Dictionary<string, string> { ["reason"] = "kept-only-with-a-valid-code" });
            CommandResponse<TestResult> response =
                CommandResponse.Failed<TestResult>("req-invalid", "pack", 10, fault);
            AssertEx.Equal(10, response.ExitCode);
            AssertEx.Equal("RCLI5000", response.Fault!.Code);
            AssertEx.Equal("The command failed unexpectedly.", response.Fault.Message);
            AssertEx.Equal(0, response.Fault.Details.Count);

            byte[] frame = CommandJsonEnvelopeWriter.Serialize(response, TestJsonContext.Default.TestResult);
            using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
            AssertEx.Equal(10, document.RootElement.GetProperty("exitCode").GetInt32());
            AssertEx.Equal("RCLI5000", document.RootElement.GetProperty("fault").GetProperty("code").GetString());
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask DispatcherHumanUnsafeFaultKeepsCode()
    {
        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Failed<TestResult>(
                "req-human-unsafe",
                "pack",
                10,
                new CommandFault("RAS1001", "Could not read /home/ada/atlas.png.")),
            new TestCodec());
        AssertEx.Equal(string.Empty, console.StandardOutput);
        AssertEx.Equal("RAS1001: The command failed; details were redacted.\n", console.StandardError);
    }


    private static byte[] SuccessFrame() => CommandJsonEnvelopeWriter.Serialize(
        CommandResponse.Succeeded(
            "req-1",
            "status",
            TestCodec.Identity,
            new TestResult(42, "ok")),
        TestJsonContext.Default.TestResult);
}
