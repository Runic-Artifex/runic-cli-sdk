using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Runic.CommandLine.Tests;

// CommandFault.HelpUri and CommandDiagnostic.HelpUri: validated https links that JSON and human
// output keep, while messages, details and arguments stay redacted.
internal static class HelpLinkTests
{
    private static readonly Uri FaultLink = new("https://docs.example.com/errors#ras1001");
    private static readonly Uri DiagnosticLink = new("https://docs.example.com/errors#ras1002");
    private static readonly string[] FaultProperties = ["code", "message", "details", "retryable", "helpUri"];
    private static readonly string[] DiagnosticProperties =
        ["code", "kind", "commandPath", "messageKey", "message", "phase", "severity", "tokenIndex", "arguments", "helpUri"];

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("help-links/only-absolute-https-links-are-accepted", OnlyHttpsLinksAreAccepted),
        new("help-links/json-writes-links-after-the-existing-members", JsonWritesLinks),
        new("help-links/json-omits-absent-links", JsonOmitsAbsentLinks),
        new("help-links/redaction-of-messages-is-unchanged", RedactionIsUnchanged),
        new("help-links/reader-round-trips-links", ReaderRoundTripsLinks),
        new("help-links/reader-rejects-invalid-links", ReaderRejectsInvalidLinks),
        new("help-links/human-output-writes-a-help-line", HumanOutputWritesHelpLine),
        new("help-links/human-output-writes-a-shared-link-once", HumanOutputWritesSharedLinkOnce),
        new("help-links/localization-keeps-links", LocalizationKeepsLinks),
    ];

    private static CommandFault Fault(string message = "The archive could not be read.") =>
        new("RAS1001", message, new Dictionary<string, string> { ["archive"] = "assets" }) { HelpUri = FaultLink };

    private static CommandDiagnostic Diagnostic(string message = "The archive index is missing.") =>
        new("RCLI8002", "index-missing", message, CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning,
            arguments: ["index"]) { HelpUri = DiagnosticLink };

    private static byte[] FailureFrame(CommandFault fault, params CommandDiagnostic[] diagnostics) =>
        CommandJsonEnvelopeWriter.Serialize(
            CommandResponse.Failed<TestResult>("req-help", "pack", 10, fault, diagnostics),
            TestJsonContext.Default.TestResult);

    private static ValueTask OnlyHttpsLinksAreAccepted()
    {
        AssertEx.Equal(FaultLink, Fault().HelpUri);
        AssertEx.Equal(null, new CommandFault("RAS1001", "Failed.").HelpUri);
        AssertEx.Equal(DiagnosticLink, (Diagnostic() with { }).HelpUri);
        foreach (string invalid in new[]
        {
            "http://docs.example.com/errors", "ftp://docs.example.com/errors", "file:///home/ada/errors.md",
            "https://user:secret@docs.example.com/errors", "https://docs.example.com/" + new string('a', 2_048),
        })
        {
            var uri = new Uri(invalid);
            AssertEx.Throws<ArgumentException>(() => new CommandFault("RAS1001", "Failed.") { HelpUri = uri });
            AssertEx.Throws<ArgumentException>(() => Diagnostic() with { HelpUri = uri });
        }

        AssertEx.Throws<ArgumentException>(() => Fault() with { HelpUri = new Uri("/errors", UriKind.Relative) });
        return ValueTask.CompletedTask;
    }

    private static ValueTask JsonWritesLinks()
    {
        byte[] frame = FailureFrame(Fault(), Diagnostic());
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        JsonElement fault = document.RootElement.GetProperty("fault");
        AssertEx.SequenceEqual(FaultProperties,
            fault.EnumerateObject().Select(static property => property.Name).ToArray());
        AssertEx.Equal(FaultLink.AbsoluteUri, fault.GetProperty("helpUri").GetString());
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostics")[0];
        AssertEx.SequenceEqual(
            DiagnosticProperties,
            diagnostic.EnumerateObject().Select(static property => property.Name).ToArray());
        AssertEx.Equal(DiagnosticLink.AbsoluteUri, diagnostic.GetProperty("helpUri").GetString());
        return ValueTask.CompletedTask;
    }

    private static ValueTask JsonOmitsAbsentLinks()
    {
        byte[] frame = FailureFrame(new CommandFault("RAS1001", "Failed."),
            new CommandDiagnostic("RCLI8002", "index-missing", "Missing.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning));
        AssertEx.True(!Encoding.UTF8.GetString(frame).Contains("helpUri", StringComparison.Ordinal), "An absent help link was written.");
        return ValueTask.CompletedTask;
    }

    private static ValueTask RedactionIsUnchanged()
    {
        // A URL or path in presentation text is still redacted; only the help link member is exempt.
        byte[] frame = FailureFrame(
            Fault("See https://intranet.example.com/home/ada for /home/ada/assets."),
            Diagnostic("Read https://intranet.example.com/x."));
        using JsonDocument document = JsonDocument.Parse(frame.AsMemory(0, frame.Length - 1));
        JsonElement fault = document.RootElement.GetProperty("fault");
        AssertEx.Equal("The command failed; details were redacted.", fault.GetProperty("message").GetString());
        AssertEx.Equal(FaultLink.AbsoluteUri, fault.GetProperty("helpUri").GetString());
        JsonElement diagnostic = document.RootElement.GetProperty("diagnostics")[0];
        AssertEx.Equal("The diagnostic content was redacted.", diagnostic.GetProperty("message").GetString());
        AssertEx.Equal(0, diagnostic.GetProperty("arguments").GetArrayLength());
        AssertEx.Equal(DiagnosticLink.AbsoluteUri, diagnostic.GetProperty("helpUri").GetString());
        return ValueTask.CompletedTask;
    }

    private static ValueTask ReaderRoundTripsLinks()
    {
        CommandResponse<TestResult> response = CommandJsonEnvelopeReader.Read(
            FailureFrame(Fault(), Diagnostic()), TestCodec.Identity, TestJsonContext.Default.TestResult);
        AssertEx.Equal(FaultLink, response.Fault!.HelpUri);
        AssertEx.Equal(DiagnosticLink, response.Diagnostics[0].HelpUri);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ReaderRejectsInvalidLinks()
    {
        string json = Encoding.UTF8.GetString(FailureFrame(Fault(), Diagnostic())).TrimEnd('\n');
        foreach (string replacement in new[]
        {
            "\"http://docs.example.com/errors#ras1001\"", "\"https://docs.example.com/a b\"", "\"/errors#ras1001\"",
            "\"https://user:secret@docs.example.com/\"", "\"\"", "null", "42",
        })
        {
            foreach (string link in new[] { FaultLink.AbsoluteUri, DiagnosticLink.AbsoluteUri })
            {
                byte[] frame = Encoding.UTF8.GetBytes(json.Replace("\"" + link + "\"", replacement, StringComparison.Ordinal) + "\n");
                CommandProtocolException exception = AssertEx.Throws<CommandProtocolException>(() =>
                    CommandJsonEnvelopeReader.Read(frame, TestCodec.Identity, TestJsonContext.Default.TestResult));
                AssertEx.Equal("invalid-help-uri", exception.Kind, replacement);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask HumanOutputWritesHelpLine()
    {
        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(CommandOutputMode.Human, console, CultureInfo.InvariantCulture,
            CommandResponse.Failed<TestResult>("req-help", "pack", 10, Fault(), [Diagnostic()]), new TestCodec());
        AssertEx.Equal(
            "RCLI8002: The archive index is missing.\nHelp for RCLI8002: https://docs.example.com/errors#ras1002\n" +
            "RAS1001: The archive could not be read.\nHelp for RAS1001: https://docs.example.com/errors#ras1001\n",
            console.StandardError);

        var success = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(CommandOutputMode.Human, success, CultureInfo.InvariantCulture,
            CommandResponse.Succeeded("req-help", "pack", TestCodec.Identity, new TestResult(7, "ok"), [Diagnostic()]), new TestCodec());
        AssertEx.Equal("7:ok\n", success.StandardOutput);
        AssertEx.Equal("RCLI8002: The archive index is missing.\nHelp for RCLI8002: https://docs.example.com/errors#ras1002\n", success.StandardError);
    }

    private static async ValueTask HumanOutputWritesSharedLinkOnce()
    {
        // The diagnostic line already reports the fault and its link.
        var console = new MemoryCommandConsole();
        var fault = new CommandFault("RCLI8001", "The archive could not be read.") { HelpUri = FaultLink };
        var diagnostic = new CommandDiagnostic("RCLI8001", "archive-unreadable", "The archive could not be read.",
            CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Error) { HelpUri = FaultLink };
        await CommandOutputDispatcher.DispatchAsync(CommandOutputMode.Human, console, CultureInfo.InvariantCulture,
            CommandResponse.Failed<TestResult>("req-help", "pack", 10, fault, [diagnostic]), new TestCodec());
        AssertEx.Equal("RCLI8001: The archive could not be read.\nHelp for RCLI8001: https://docs.example.com/errors#ras1001\n", console.StandardError);

        // Without a link on the diagnostic, the fault's link still follows it.
        var unlinked = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(CommandOutputMode.Human, unlinked, CultureInfo.InvariantCulture,
            CommandResponse.Failed<TestResult>("req-help", "pack", 10, fault, [diagnostic with { HelpUri = null }]), new TestCodec());
        AssertEx.Equal("RCLI8001: The archive could not be read.\nHelp for RCLI8001: https://docs.example.com/errors#ras1001\n", unlinked.StandardError);
    }

    private static async ValueTask LocalizationKeepsLinks()
    {
        var context = new CommandTextContext(CultureInfo.GetCultureInfo("de-DE"), new Resolver());
        CommandDiagnostic localized = context.Localize(Diagnostic());
        AssertEx.Equal("Übersetzt.", localized.Message);
        AssertEx.Equal(DiagnosticLink, localized.HelpUri);

        var console = new MemoryCommandConsole();
        await CommandOutputDispatcher.DispatchAsync(CommandOutputMode.Human, console, CultureInfo.InvariantCulture,
            CommandResponse.Failed<TestResult>("req-help", "pack", 10, Fault()), new TestCodec(), context);
        AssertEx.Equal("RAS1001: Übersetzt.\nHelp for RAS1001: https://docs.example.com/errors#ras1001\n", console.StandardError);
    }

    private sealed class Resolver : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) => "Übersetzt.";
    }
}
