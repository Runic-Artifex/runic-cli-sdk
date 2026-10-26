using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Runic.CommandLine.Tests;

internal sealed class OutputTests
{
    private static readonly string[] EnvelopeProperties =
        ["protocol", "requestId", "command", "success", "exitCode", "payloadType", "payload", "fault", "diagnostics"];
    private static readonly string[] OrderedDetailKeys = ["Alpha", "alpha", "zulu"];
    private static readonly string[] DiagnosticProperties =
        ["code", "kind", "commandPath", "messageKey", "message", "phase", "severity", "tokenIndex", "arguments"];

    [Test, DisplayName("output/json-frame-is-one-pure-utf8-object")]
    public ValueTask JsonFramePurity()
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

    [Test, DisplayName("output/json-property-order-is-deterministic")]
    public ValueTask DeterministicPropertyOrder()
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

    [Test, DisplayName("output/faults-and-diagnostics-are-sanitized")]
    public ValueTask Sanitization()
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
            CommandResponse.Failure<TestResult>("req-2", "status", 10, fault, [diagnostic]),
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

    // CommandDiagnosticSeverity starts at Information = 0 since 0.8; the envelope writes and reads the same names.
    [Test, DisplayName("output/severity-wire-names-do-not-follow-the-enum-values")]
    public ValueTask SeverityWireNames()
    {
        CommandDiagnosticSeverity[] severities =
            [CommandDiagnosticSeverity.Error, CommandDiagnosticSeverity.Warning, CommandDiagnosticSeverity.Information, default];
        CommandDiagnostic[] diagnostics = severities.Select(static severity => new CommandDiagnostic(
            "RCLI2001", "invalid-value", "The value is invalid.", CommandDiagnosticPhase.Binding, severity)).ToArray();
        byte[] frame = CommandJsonEnvelopeWriter.Serialize(
            CommandResponse.Failure<TestResult>("req-3", "status", 1, new CommandFault("RCLI3000", "Failed."), diagnostics),
            TestJsonContext.Default.TestResult);
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        AssertEx.Equal(CommandProtocol.Identity, document.RootElement.GetProperty("protocol").GetString());
        AssertEx.SequenceEqual(
            ["error", "warning", "information", "information"],
            document.RootElement.GetProperty("diagnostics").EnumerateArray()
                .Select(static diagnostic => diagnostic.GetProperty("severity").GetString()!).ToArray());
        CommandResponse<TestResult> read = CommandJsonEnvelopeReader.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult);
        AssertEx.SequenceEqual(severities, read.Diagnostics.Select(static diagnostic => diagnostic.Severity).ToArray());
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output/fault-details-use-ordinal-order")]
    public ValueTask DetailOrdering()
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
            CommandResponse.Failure<TestResult>("req-3", "status", 10, fault),
            TestJsonContext.Default.TestResult);
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        AssertEx.SequenceEqual(
            OrderedDetailKeys,
            document.RootElement.GetProperty("fault").GetProperty("details")
                .EnumerateObject().Select(static property => property.Name));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output/source-generated-payload-round-trips")]
    public ValueTask SourceGeneratedRoundTrip()
    {
        byte[] frame = SuccessFrame();
        CommandResponse<TestResult> response = CommandJsonEnvelopeReader.Read(
            frame,
            TestCodec.Identity,
            TestJsonContext.Default.TestResult);
        AssertEx.True(response.Succeeded);
        AssertEx.Equal(new TestResult(42, "ok"), response.Payload);

        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(frame, "different/1", TestJsonContext.Default.TestResult));
        AssertEx.Equal("unsupported-payload-type", exception.Kind);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output/reader-accepts-unknown-additive-members")]
    public ValueTask ReaderAcceptsAdditiveMembers()
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

    [Test, DisplayName("output/reader-rejects-impure-framing-and-invalid-utf8")]
    public ValueTask ReaderRejectsImpureFrames()
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

    [Test, DisplayName("output/reader-rejects-duplicate-and-oversized-envelopes")]
    public ValueTask ReaderRejectsDuplicateAndOversized()
    {
        byte[] original = SuccessFrame();
        string json = Encoding.UTF8.GetString(original.AsSpan(0, original.Length - 2));
        byte[] duplicate = Encoding.UTF8.GetBytes($"{json},\"protocol\":\"{CommandProtocol.Identity}\"}}\n");
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

    [Test, DisplayName("output/reader-checks-payload-identity-before-deserialization")]
    public ValueTask ReaderChecksIdentityFirst()
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

    [Test, DisplayName("output/reader-rejects-unsafe-fault-content")]
    public ValueTask ReaderRejectsUnsafeFault()
    {
        const string Unsafe = "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"status\",\"success\":false,\"exitCode\":10,\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI3000\",\"message\":\"bad\\u001btext\",\"details\":{},\"retryable\":false},\"diagnostics\":[]}\n";
        AssertProtocolKind(Encoding.UTF8.GetBytes(Unsafe), "unsafe-fault-content");
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output/writer-rejects-payload-over-one-mebibyte")]
    public ValueTask WriterRejectsOversizedPayload()
    {
        var response = CommandResponse.Success(
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

    [Test, DisplayName("output/dispatcher-keeps-json-off-stderr")]
    public async ValueTask DispatcherJsonChannels()
    {
        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Json,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Success(
                "req-json", "status", TestCodec.Identity, new TestResult(42, "ok")),
            new TestCodec());
        AssertEx.True(console.StandardOutput.StartsWith('{'));
        AssertEx.True(console.StandardOutput.EndsWith('\n'));
        AssertEx.Equal(string.Empty, console.StandardError);
    }

    [Test, DisplayName("output/dispatcher-keeps-human-fault-off-stdout")]
    public async ValueTask DispatcherHumanChannels()
    {
        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Failure<TestResult>(
                "req-human",
                "status",
                10,
                new CommandFault("RCLI3000", "Expected failure.")),
            new TestCodec());
        AssertEx.Equal(string.Empty, console.StandardOutput);
        AssertEx.Equal("RCLI3000: Expected failure.\n", console.StandardError);
    }

    [Test, DisplayName("output/dispatcher-writes-success-diagnostics-to-human-stderr")]
    public async ValueTask DispatcherHumanSuccessDiagnostics()
    {
        var console = new MemoryCommandConsole();
        var diagnostic = new CommandDiagnostic(
            "RCLI3001", "deprecated-option", "The option is deprecated.",
            CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning);
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Success(
                "req-human-warning", "status", TestCodec.Identity, new TestResult(7, "ok"), [diagnostic]),
            new TestCodec());

        AssertEx.Equal("7:ok\n", console.StandardOutput);
        AssertEx.Equal("RCLI3001: The option is deprecated.\n", console.StandardError);
    }

    [Test, DisplayName("output/dispatcher-does-not-repeat-a-fault-diagnostic")]
    public async ValueTask DispatcherDoesNotRepeatFaultDiagnostic()
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
            CommandResponse.Failure<TestResult>("req-human-fault", "status", 10, fault, [diagnostic]),
            new TestCodec());

        AssertEx.Equal("RCLI3000: Expected failure.\n", console.StandardError);
    }

    [Test, DisplayName("output/unsafe-fault-message-keeps-well-formed-code")]
    public ValueTask UnsafeMessageKeepsWellFormedCode()
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
                CommandResponse.Failure<TestResult>("req-keep", "pack", 10, fault);
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
            CommandFault sanitized = CommandResponse.Failure<TestResult>(
                "req-keep", "pack", 10, new CommandFault("RAS1001", message)).Fault!;
            AssertEx.Equal("RAS1001", sanitized.Code);
            AssertEx.Equal("The command failed; details were redacted.", sanitized.Message);
        }

        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output/invalid-fault-code-collapses-to-rcli5000")]
    public ValueTask InvalidCodeCollapsesToSoftwareFailure()
    {
        foreach (string code in new[] { "ras1001", "1001", "RAS 1001", "RAS/1001", "RAS\u001b1001", new string('A', 65) })
        {
            var fault = new CommandFault(
                code,
                "A safe message.",
                new Dictionary<string, string> { ["reason"] = "kept-only-with-a-valid-code" });
            CommandResponse<TestResult> response =
                CommandResponse.Failure<TestResult>("req-invalid", "pack", 10, fault);
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

    [Test, DisplayName("output/dispatcher-human-unsafe-fault-keeps-code")]
    public async ValueTask DispatcherHumanUnsafeFaultKeepsCode()
    {
        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Failure<TestResult>(
                "req-human-unsafe",
                "pack",
                10,
                new CommandFault("RAS1001", "Could not read /home/ada/atlas.png.")),
            new TestCodec());
        AssertEx.Equal(string.Empty, console.StandardOutput);
        AssertEx.Equal("RAS1001: The command failed; details were redacted.\n", console.StandardError);
    }

    [Test, DisplayName("output/unsafe-fault-detail-keys-are-dropped")]
    public ValueTask UnsafeDetailKeysAreDropped()
    {
        var fault = new CommandFault(
            "RAS1001",
            "The asset could not be packed.",
            new Dictionary<string, string>
            {
                ["innerException"] = "value",
                ["/home/ada"] = "value",
                ["C:/x"] = "value",
                [@"\\server"] = "value",
                ["reason"] = "missing-file",
            });
        byte[] frame = CommandJsonEnvelopeWriter.Serialize(
            CommandResponse.Failure<TestResult>("req-keys", "pack", 10, fault),
            TestJsonContext.Default.TestResult);
        string json = Encoding.UTF8.GetString(frame);
        AssertEx.True(!json.Contains("Exception", StringComparison.Ordinal), "An unsafe detail key was written.");
        AssertEx.True(!json.Contains("/home/", StringComparison.Ordinal), "An unsafe detail key was written.");
        AssertEx.True(!json.Contains("C:/", StringComparison.Ordinal), "An unsafe detail key was written.");

        CommandResponse<TestResult> roundTrip = CommandJsonEnvelopeReader.Read(
            frame, TestCodec.Identity, TestJsonContext.Default.TestResult);
        AssertEx.Equal("RAS1001", roundTrip.Fault!.Code);
        AssertEx.Equal("The asset could not be packed.", roundTrip.Fault.Message);
        AssertEx.Equal(1, roundTrip.Fault.Details.Count);
        AssertEx.Equal("missing-file", roundTrip.Fault.Details["reason"]);
        return ValueTask.CompletedTask;
    }

    // SEC-40: bidi overrides, isolates, zero-width and tag characters reorder or hide text on a terminal, so
    // writers replace them like control characters.
    [Test, DisplayName("output/faults-and-diagnostics-neutralize-bidi-and-invisible-format-characters")]
    public ValueTask FormatCharactersAreNeutralized()
    {
        const string Hostile = "report\u202Etxt.exe\u2066x\u2069\u200By\u202Az\u2060\uFEFF\u202D\u2068\U000E0041end";
        var fault = new CommandFault("RAS1001", Hostile, new Dictionary<string, string> { ["file"] = Hostile, ["k\u202Eey"] = "v" });
        var diagnostic = new CommandDiagnostic(
            "RCLI2001", "invalid-value", Hostile, CommandDiagnosticPhase.Binding, CommandDiagnosticSeverity.Error, 2,
            [Hostile]);
        CommandResponse<TestResult> response = CommandResponse.Failure<TestResult>("req-bidi", "pack", 10, fault, [diagnostic]);
        const string Expected = "report txt.exe x y z end";
        AssertEx.Equal(Expected, response.Fault!.Message);
        AssertEx.Equal(Expected, response.Fault.Details["file"]);
        AssertEx.Equal("v", response.Fault.Details["k ey"]);

        byte[] frame = CommandJsonEnvelopeWriter.Serialize(response, TestJsonContext.Default.TestResult);
        string json = Encoding.UTF8.GetString(frame);
        foreach (string escaped in new[] { "\\u202E", "\\u2066", "\\u2069", "\\u200B", "\\u202A", "\\u2060", "\\uFEFF", "\\u202D", "\\u2068", "\\uDB40" })
            AssertEx.True(!json.Contains(escaped, StringComparison.OrdinalIgnoreCase), "Expected no " + escaped + " in " + json);
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        JsonElement written = document.RootElement.GetProperty("diagnostics")[0];
        AssertEx.Equal(Expected, written.GetProperty("message").GetString());
        AssertEx.Equal(Expected, written.GetProperty("arguments")[0].GetString());

        // Ordinary text from other scripts is kept, with the joiners and marks that Persian, Indic and right-to-left
        // text and emoji sequences need: ZWNJ, ZWJ, LRM, RLM and ALM.
        const string Legitimate = "Ungültig – 日本語 ✓ שלום\u200F (x) می\u200Cخواهم क्\u200D 👨\u200D👩 \u200Eabc\u061C";
        AssertEx.Equal(Legitimate, CommandResponse.Failure<TestResult>(
            "req-text", "pack", 10, new CommandFault("RAS1001", Legitimate)).Fault!.Message);
        return ValueTask.CompletedTask;
    }

    // SEC-40 reader side (D046.6): the protocol's Unicode control characters include the bidi embeddings, overrides
    // and isolates, the tag characters and U+200B, U+2060 and U+FEFF, so the strict reader rejects a frame that
    // carries one in fault or diagnostic text or in the request ID, as it rejects C0 and C1 controls. The joiners
    // and marks the writer keeps are read as they are.
    [Test, DisplayName("output/reader-rejects-bidi-and-invisible-format-characters")]
    public ValueTask ReaderRejectsFormatCharacters()
    {
        foreach (string hostile in new[] { "\\u202A", "\\u202E", "\\u2066", "\\u2069", "\\u200B", "\\u2060", "\\uFEFF", "\\uDB40\\uDC41", "\\uDB40\\uDC7F" })
        {
            AssertEx.Equal("unsafe-fault-content", ReadKind(FaultFrame("bad" + hostile + "text", "\"x\":\"y\"")), hostile);
            AssertEx.Equal("unsafe-fault-content", ReadKind(FaultFrame("bad", "\"x\":\"y" + hostile + "\"")), hostile);
            AssertEx.Equal("unsafe-fault-content", ReadKind(FaultFrame("bad", "\"k" + hostile + "\":\"y\"")), hostile);
            AssertEx.Equal("unsafe-diagnostic-content", ReadKind(DiagnosticFrame("bad" + hostile, "ok")), hostile);
            AssertEx.Equal("unsafe-diagnostic-content", ReadKind(DiagnosticFrame("bad", "o" + hostile + "k")), hostile);
            AssertEx.Equal("invalid-request-id", ReadKind(Encoding.UTF8.GetBytes(
                "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req" + hostile + "\",\"command\":\"status\",\"success\":false,\"exitCode\":10,\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI3000\",\"message\":\"bad\",\"details\":{},\"retryable\":false},\"diagnostics\":[]}\n")), hostile);
        }

        // Unescaped UTF-8 is rejected too.
        AssertEx.Equal("unsafe-fault-content", ReadKind(FaultFrame("bad\u202Etext", "\"x\":\"y\"")));
        AssertEx.Throws<ArgumentException>(() => CommandResponse.Success("req\u2066", "status", TestCodec.Identity, new TestResult(1, "x")));

        const string Legitimate = "\u0645\u06CC\u200C\u062E\u0648\u0627\u0647\u0645 \u0915\u094D\u200D \u200Eabc\u200F \u061C";
        CommandResponse<TestResult> read = CommandJsonEnvelopeReader.Read(
            FaultFrame(Legitimate, "\"x\":\"" + Legitimate + "\""), TestCodec.Identity, TestJsonContext.Default.TestResult);
        AssertEx.Equal(Legitimate, read.Fault!.Message);
        AssertEx.Equal(Legitimate, read.Fault.Details["x"]);
        return ValueTask.CompletedTask;

        static byte[] FaultFrame(string message, string details) => Encoding.UTF8.GetBytes(
            "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"status\",\"success\":false,\"exitCode\":10,\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI3000\",\"message\":\"" + message + "\",\"details\":{" + details + "},\"retryable\":false},\"diagnostics\":[]}\n");

        static byte[] DiagnosticFrame(string message, string argument) => Encoding.UTF8.GetBytes(
            "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"status\",\"success\":false,\"exitCode\":2,\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI2001\",\"message\":\"bad\",\"details\":{},\"retryable\":false},\"diagnostics\":[{\"code\":\"RCLI2001\",\"kind\":\"invalid-value\",\"commandPath\":[\"status\"],\"messageKey\":\"diagnostics.invalid-value\",\"message\":\"" + message + "\",\"phase\":\"binding\",\"severity\":\"error\",\"tokenIndex\":1,\"arguments\":[\"" + argument + "\"]}]}\n");

        static string ReadKind(byte[] frame) => AssertEx.Throws<CommandProtocolException>(() =>
            CommandJsonEnvelopeReader.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult)).Kind;
    }

    // SEC-43, protocol §Compatibility: duplicate property names are invalid at every level, also in the payload.
    [Test, DisplayName("output/reader-rejects-duplicate-payload-members")]
    public ValueTask ReaderRejectsDuplicatePayloadMembers()
    {
        byte[] frame = SuccessFrame();
        string json = Encoding.UTF8.GetString(frame);
        int payload = json.IndexOf("\"payload\":{", StringComparison.Ordinal) + "\"payload\":{".Length;
        AssertEx.True(payload > "\"payload\":{".Length, json);
        AssertEx.Equal(TestCodec.Identity, CommandJsonEnvelopeReader.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult).PayloadType);
        foreach (string member in new[] { "\"x\":1,\"x\":2,", "\"nested\":[{\"a\":1,\"a\":1}]," })
        {
            byte[] duplicated = Encoding.UTF8.GetBytes(json.Insert(payload, member));
            AssertProtocolKind(duplicated, "duplicate-property");
        }

        // Unknown payload members without duplicates stay accepted.
        byte[] unknown = Encoding.UTF8.GetBytes(json.Insert(payload, "\"x\":1,\"nested\":[{\"a\":1,\"b\":1}],"));
        AssertEx.Equal(TestCodec.Identity, CommandJsonEnvelopeReader.Read(unknown, TestCodec.Identity, TestJsonContext.Default.TestResult).PayloadType);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output/control-only-fault-message-is-redacted")]
    public ValueTask ControlOnlyMessageIsRedacted()
    {
        CommandFault sanitized = CommandResponse.Failure<TestResult>(
            "req-control", "pack", 10, new CommandFault("RAS1001", "\u001b\u0007\u0000")).Fault!;
        AssertEx.Equal("RAS1001", sanitized.Code);
        AssertEx.Equal("The command failed; details were redacted.", sanitized.Message);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output/dispatcher-does-not-repeat-a-redacted-fault-diagnostic")]
    public async ValueTask DispatcherDoesNotRepeatRedactedFaultDiagnostic()
    {
        var console = new MemoryCommandConsole();
        var fault = new CommandFault("RCLI8001", "Could not read /home/ada/atlas.png.");
        var diagnostic = new CommandDiagnostic(
            "RCLI8001", "asset-missing", "Could not read /home/ada/atlas.png.",
            CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Error);
        await CommandOutputDispatcher.DispatchAsync(
            CommandOutputMode.Human,
            console,
            CultureInfo.InvariantCulture,
            CommandResponse.Failure<TestResult>("req-human-redacted", "pack", 10, fault, [diagnostic]),
            new TestCodec());

        AssertEx.Equal("RCLI8001: The diagnostic content was redacted.\n", console.StandardError);
    }

    private static byte[] SuccessFrame() => CommandJsonEnvelopeWriter.Serialize(
        CommandResponse.Success(
            "req-1",
            "status",
            TestCodec.Identity,
            new TestResult(42, "ok")),
        TestJsonContext.Default.TestResult);
}
