using System.Text;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// CommandTestEnvelope.Read<T>: the core reader's checks plus no member the protocol does not define.
internal sealed class TestingEnvelopeTests
{
    private const string Success =
        "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"status\",\"success\":true,\"exitCode\":0," +
        "\"payloadType\":\"tests.result/1\",\"payload\":{\"value\":42,\"text\":\"ok\"},\"fault\":null,\"diagnostics\":[]}\n";

    private const string Failure =
        "{\"protocol\":\"runic.commandline/1\",\"requestId\":\"req\",\"command\":\"clone\",\"success\":false,\"exitCode\":10," +
        "\"payloadType\":null,\"payload\":null,\"fault\":{\"code\":\"RCLI3000\",\"message\":\"The clone failed.\",\"details\":{}," +
        "\"retryable\":false,\"helpUri\":\"https://docs.example.com/errors\",\"data\":{\"type\":\"tests.recovery/1\",\"payload\":{\"any\":1}}}," +
        "\"diagnostics\":[{\"code\":\"RCLI8002\",\"kind\":\"index-missing\",\"commandPath\":[\"clone\"],\"messageKey\":\"diagnostics.index\"," +
        "\"message\":\"The index is missing.\",\"phase\":\"execution\",\"severity\":\"warning\",\"tokenIndex\":null,\"arguments\":[]," +
        "\"helpUri\":\"https://docs.example.com/errors\"}]}\n";

    [Test, DisplayName("testing/envelope-read-returns-the-typed-success-payload")]
    public ValueTask ReadsSuccess()
    {
        CommandResponse<TestResult> response = CommandTestEnvelope.Read(Success, TestCodec.Identity, TestJsonContext.Default.TestResult);
        AssertEx.True(response.Succeeded);
        AssertEx.Equal("tests.result/1", response.PayloadType);
        AssertEx.Equal(new TestResult(42, "ok"), response.Payload);

        string written = Encoding.UTF8.GetString(CommandJsonEnvelopeWriter.Serialize(
            CommandResponse.Success("req-1", "status", TestCodec.Identity, new TestResult(7, "seven")),
            TestJsonContext.Default.TestResult));
        AssertEx.Equal(new TestResult(7, "seven"), CommandTestEnvelope.Read(written, TestCodec.Identity, TestJsonContext.Default.TestResult).Payload);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("testing/envelope-read-accepts-every-defined-optional-member")]
    public ValueTask ReadsFailureWithOptionalMembers()
    {
        CommandResponse<TestResult> response = CommandTestEnvelope.Read(Failure, TestCodec.Identity, TestJsonContext.Default.TestResult);
        AssertEx.True(!response.Succeeded);
        AssertEx.Equal(10, response.ExitCode);
        AssertEx.Equal("RCLI3000", response.Fault!.Code);
        AssertEx.Equal("tests.recovery/1", response.FailureData!.Type);
        AssertEx.Equal("index-missing", response.Diagnostics.Single().Kind);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("testing/envelope-read-rejects-unknown-members-the-core-reader-ignores")]
    public ValueTask RejectsUnknownMembers()
    {
        string[] frames =
        [
            Success.Replace("\"diagnostics\":[]", "\"diagnostics\":[],\"extra\":1", StringComparison.Ordinal),
            Failure.Replace("\"retryable\":false", "\"retryable\":false,\"extra\":1", StringComparison.Ordinal),
            Failure.Replace("\"type\":\"tests.recovery/1\"", "\"type\":\"tests.recovery/1\",\"extra\":1", StringComparison.Ordinal),
            Failure.Replace("\"arguments\":[]", "\"arguments\":[],\"extra\":1", StringComparison.Ordinal),
        ];
        foreach (string frame in frames)
        {
            AssertEx.True(frame.Contains("\"extra\":1", StringComparison.Ordinal));
            _ = CommandJsonEnvelopeReader.Read(Encoding.UTF8.GetBytes(frame), TestCodec.Identity, TestJsonContext.Default.TestResult);
            CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(
                () => CommandTestEnvelope.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult));
            AssertEx.Equal("unknown-member", exception.Kind);
            AssertEx.True(exception.Message.Contains("'extra'", StringComparison.Ordinal), exception.Message);
        }

        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("testing/envelope-read-rejects-missing-members-wrong-types-and-bad-frames")]
    public ValueTask RejectsInvalidEnvelopes()
    {
        AssertKind(Success.Replace(",\"diagnostics\":[]", "", StringComparison.Ordinal), "missing-required-field");
        AssertKind(Success.Replace("\"requestId\":\"req\",", "", StringComparison.Ordinal), "missing-required-field");
        AssertKind(Failure.Replace("\"retryable\":false,", "", StringComparison.Ordinal), "missing-required-field");
        AssertKind(Success.Replace("\"exitCode\":0", "\"exitCode\":\"0\"", StringComparison.Ordinal), "invalid-envelope");
        AssertKind(Success.Replace("\"success\":true", "\"success\":1", StringComparison.Ordinal), "invalid-envelope");
        AssertKind(Success.Replace("\"value\":42", "\"value\":\"42\"", StringComparison.Ordinal), "payload-shape-mismatch");
        AssertKind(Success.Replace("\"payloadType\":\"tests.result/1\"", "\"payloadType\":\"tests.other/1\"", StringComparison.Ordinal), "unsupported-payload-type");
        AssertKind(Success.Replace("\"payloadType\":\"tests.result/1\"", "\"payloadType\":null", StringComparison.Ordinal), "missing-payload-type");
        AssertKind(Success.Replace("\"payloadType\":\"tests.result/1\"", "\"payloadType\":\"Tests/1\"", StringComparison.Ordinal), "invalid-payload-type");
        AssertKind(Success.Replace("\"payloadType\":\"tests.result/1\"", "\"payloadType\":7", StringComparison.Ordinal), "invalid-envelope");
        AssertKind(Success.Replace("\"exitCode\":0", "\"exitCode\":0,\"exitCode\":0", StringComparison.Ordinal), "duplicate-property");
        AssertKind(Success.TrimEnd('\n'), "invalid-framing");
        AssertKind(Success + "\n", "invalid-framing");
        AssertKind("{\"protocol\":", "invalid-framing");
        AssertKind("{\"protocol\":\n", "malformed-json");
        AssertKind(Success.Replace("\"ok\"", "\"\ud800\"", StringComparison.Ordinal), "invalid-utf8");
        AssertEx.Throws<ArgumentException>(() => CommandTestEnvelope.Read(" ", TestCodec.Identity, TestJsonContext.Default.TestResult));
        AssertEx.Throws<ArgumentNullException>(() => CommandTestEnvelope.Read<TestResult>(Success, TestCodec.Identity, null!));
        AssertEx.Throws<ArgumentException>(() => CommandTestEnvelope.Read(Success, " ", TestJsonContext.Default.TestResult));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("testing/envelope-read-reads-application-output")]
    public async ValueTask ReadsApplicationOutput()
    {
        var tester = new CommandAppTester(console => new CommandApp(GeneratedCommandCatalog.Create())
        {
            Console = console, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false,
        });
        CommandAppTestResult result = await tester.RunAsync(["report", "--format=json"]);
        CommandResponse<SampleReport> response = CommandTestEnvelope.Read(result.StandardOutput, "sample.report/1", SampleJsonContext.Default.SampleReport);
        AssertEx.Equal("sample.report/1", response.PayloadType);
        AssertEx.Equal(new SampleReport("Ready", 2), response.Payload);

        // The catalog's default command answers an unknown word with its own payload type.
        result = await tester.RunAsync(["no-such-command", "--format=json"]);
        CommandProtocolException mismatch = AssertEx.Throws<CommandProtocolException>(
            () => CommandTestEnvelope.Read(result.StandardOutput, "sample.report/1", SampleJsonContext.Default.SampleReport));
        AssertEx.Equal("unsupported-payload-type", mismatch.Kind);

        result = await tester.RunAsync(["report", "--no-such-option", "--format=json"]);
        CommandResponse<SampleReport> failure = CommandTestEnvelope.Read(result.StandardOutput, "sample.report/1", SampleJsonContext.Default.SampleReport);
        AssertEx.Equal(result.ExitCode, failure.ExitCode);
        AssertEx.True(!failure.Succeeded && failure.Fault is not null && failure.Diagnostics.Count > 0);
    }

    private static void AssertKind(string frame, string kind)
    {
        CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(
            () => CommandTestEnvelope.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult));
        AssertEx.Equal(kind, exception.Kind);
    }
}
