using System.Globalization;
using System.Text;
using System.Text.Json;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// runic-cli-sdk#89: one JSON document on stdout for every outcome, an optional quiet stderr, and pure ASCII output.
internal sealed class JsonEveryOutcomeTests
{
    private const string Rooms = "Räume \U0001F600";

    // The default escaping writes letters as UTF-8; characters beyond the Basic Multilingual Plane are escaped.
    private const string RoomsJson = "\"Räume \\uD83D\\uDE00\"\n";

    [Command("jo-rooms", Hidden = true)]
    internal static string RoomsCommand() => Rooms;

    [Command("jo-validate", Hidden = true)]
    internal static CommandOutcome<string> Validate() =>
        CommandOutcome.Failure<string>(CommandExitCategory.Validation, new CommandFault("JO_RANGE", "--since must not be later than --until."));

    private static readonly string[] RoomsRow = ["Räume"];

    [Command("jo-domain", Hidden = true)]
    internal static CommandOutcome<string> Domain([Option("--data")] bool data = false) =>
        CommandOutcome.Failure<string>(
            CommandExitCategory.CommandFailure,
            new CommandFault("JO_REFUSED", "Die Räume sind gesperrt."),
            [new CommandDiagnostic("RCLI3997", "note", "Ein Raum wurde übersprungen.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning)],
            "Error: refused.\n",
            data ? CommandFailureData.Create("tests.rooms/1", RoomsRow, RecoveryJsonContext.Default.StringArray) : null);

    // Usage, validation, domain failure and an unexpected exception, as #89 lists them.
    private static readonly (string[] Args, int ExitCode, string FaultCode)[] Failures =
    [
        (["jo-rooms", "--bogus"], CommandExitCodes.Usage, "RCLI1001"),
        (["jo-validate"], CommandExitCodes.Validation, "JO_RANGE"),
        (["jo-domain"], CommandExitCodes.CommandFailure, "JO_REFUSED"),
        (["fail"], CommandExitCodes.HostFailure, "RCLI5000"),
    ];

    private static CommandApp App(ICommandConsole console, CommandJsonOutputOptions? json = null, bool payloadAlias = false) =>
        new(GeneratedCommandCatalog.Create(builder =>
        {
            if (payloadAlias) builder.JsonOutputAlias("--json", JsonOutputShape.Payload);
        }))
        {
            Console = console,
            Version = "1.2.3-ä",
            ParseSettings = ParseSettings.Default,
            HandleTerminationSignals = false,
            Culture = CultureInfo.InvariantCulture,
            JsonOutput = json ?? CommandJsonOutputOptions.Default,
        };

    [Test, DisplayName("json-output/envelope-answers-every-outcome")]
    public async ValueTask EnvelopeAnswersEveryOutcome()
    {
        foreach (CommandJsonOutputOptions json in new[] { CommandJsonOutputOptions.Default, new CommandJsonOutputOptions { QuietStandardError = true } })
        {
            foreach ((string[] args, int exitCode, string faultCode) in Failures)
            {
                string label = string.Join(' ', args) + (json.QuietStandardError ? " (quiet)" : "");
                var console = new TestCommandConsole();
                AssertEx.Equal(exitCode, await App(console, json).RunAsync([.. args, "--format", "json"]), label);
                using (JsonDocument document = CommandTestEnvelope.Parse(console.StandardOutput))
                {
                    AssertEx.Equal(faultCode, document.RootElement.GetProperty("fault").GetProperty("code").GetString(), label);
                    AssertEx.Equal(exitCode, document.RootElement.GetProperty("exitCode").GetInt32(), label);
                }

                AssertEx.Equal("", console.StandardError, label);
            }
        }

        // A command's own console text moves to stderr in JSON mode; the quiet option drops it.
        var noisy = new TestCommandConsole();
        AssertEx.Equal(0, await App(noisy).RunAsync(["noisy", "--format", "json"]));
        AssertEx.Equal("Working\n", noisy.StandardError);
        noisy = new TestCommandConsole();
        AssertEx.Equal(0, await App(noisy, new CommandJsonOutputOptions { QuietStandardError = true }).RunAsync(["noisy", "--format", "json"]));
        using (CommandTestEnvelope.Parse(noisy.StandardOutput))
        {
        }

        AssertEx.Equal("", noisy.StandardError);

        // Human output ignores the quiet option.
        var human = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.CommandFailure, await App(human, new CommandJsonOutputOptions { QuietStandardError = true }).RunAsync(["jo-domain"]));
        AssertEx.True(human.StandardError.Contains("Die Räume sind gesperrt.", StringComparison.Ordinal), human.StandardError);
    }

    [Test, DisplayName("json-output/payload-shape-answers-every-outcome-with-the-option")]
    public async ValueTask PayloadShapeAnswersEveryOutcome()
    {
        var json = new CommandJsonOutputOptions { DocumentEveryOutcome = true, QuietStandardError = true };
        foreach ((string[] args, int exitCode, string faultCode) in Failures)
        {
            foreach (string[] spelled in new string[][] { [.. args, "--json"], ["--json", .. args] })
            {
                string label = string.Join(' ', spelled);
                var console = new TestCommandConsole();
                AssertEx.Equal(exitCode, await App(console, json, payloadAlias: true).RunAsync(spelled), label);
                using (JsonDocument document = CommandTestEnvelope.Parse(console.StandardOutput))
                {
                    AssertEx.Equal(CommandProtocol.Identity, document.RootElement.GetProperty("protocol").GetString(), label);
                    AssertEx.Equal(faultCode, document.RootElement.GetProperty("fault").GetProperty("code").GetString(), label);
                    AssertEx.Equal(exitCode, document.RootElement.GetProperty("exitCode").GetInt32(), label);
                }

                AssertEx.Equal("", console.StandardError, label);
            }
        }

        // Every usage error the parser reports for a command line that gives the flag.
        foreach (string[] args in new[] { new[] { "jo-rooms", "--json", "--json" }, ["jo-rooms", "--json", "--format", "json"], ["--json", "jo-rooms", "extra"] })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(CommandExitCodes.Usage, await App(console, json, payloadAlias: true).RunAsync(args), string.Join(' ', args));
            using (JsonDocument document = CommandTestEnvelope.Parse(console.StandardOutput))
            {
                AssertEx.True(!document.RootElement.GetProperty("success").GetBoolean());
            }

            AssertEx.Equal("", console.StandardError, string.Join(' ', args));
        }

        // Declared failure data stays the bare document.
        var data = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.CommandFailure, await App(data, json, payloadAlias: true).RunAsync(["jo-domain", "--data", "--json"]));
        AssertEx.Equal("[\"Räume\"]\n", data.StandardOutput);
        AssertEx.Equal("", data.StandardError);

        // Success documents: the payload, a JSON string for help and version, and {} for a command without a result.
        var success = new TestCommandConsole();
        AssertEx.Equal(0, await App(success, json, payloadAlias: true).RunAsync(["jo-rooms", "--json"]));
        AssertEx.Equal(RoomsJson, success.StandardOutput);
        var version = new TestCommandConsole();
        AssertEx.Equal(0, await App(version, json, payloadAlias: true).RunAsync(["--version", "--json"]));
        AssertEx.Equal("\"1.2.3-ä\"\n", version.StandardOutput);
        var help = new TestCommandConsole();
        AssertEx.Equal(0, await App(help, json, payloadAlias: true).RunAsync(["jo-rooms", "--help", "--json"]));
        using (JsonDocument document = CommandTestEnvelope.Parse(help.StandardOutput))
        {
            AssertEx.True(document.RootElement.GetString()!.StartsWith("Usage: app jo-rooms", StringComparison.Ordinal), help.StandardOutput);
        }

        var unit = new TestCommandConsole();
        AssertEx.Equal(0, await App(unit, json, payloadAlias: true).RunAsync(["noisy", "--json"]));
        AssertEx.Equal("{}\n", unit.StandardOutput);
        AssertEx.Equal("", unit.StandardError);
    }

    [Test, DisplayName("json-output/options-are-independent")]
    public async ValueTask OptionsAreIndependent()
    {
        // A document for every outcome without the quiet option keeps the human text on stderr.
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console, new CommandJsonOutputOptions { DocumentEveryOutcome = true }, payloadAlias: true)
            .RunAsync(["jo-rooms", "--bogus", "--json"]));
        using (CommandTestEnvelope.Parse(console.StandardOutput))
        {
        }

        AssertEx.True(console.StandardError.StartsWith("The option '--bogus' is not recognized.", StringComparison.Ordinal), console.StandardError);

        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.CommandFailure, await App(console, new CommandJsonOutputOptions { DocumentEveryOutcome = true }, payloadAlias: true)
            .RunAsync(["jo-domain", "--json"]));
        using (CommandTestEnvelope.Parse(console.StandardOutput))
        {
        }

        AssertEx.Equal("Ein Raum wurde übersprungen.\nError: refused.\n", console.StandardError);

        // The quiet option alone empties stderr; without a document there is then only the exit code.
        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.CommandFailure, await App(console, new CommandJsonOutputOptions { QuietStandardError = true }, payloadAlias: true)
            .RunAsync(["jo-domain", "--json"]));
        AssertEx.Equal("", console.StandardOutput);
        AssertEx.Equal("", console.StandardError);

        // Without the options, today's payload-shape behaviour: human usage error, no document.
        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(console, payloadAlias: true).RunAsync(["jo-rooms", "--bogus", "--json"]));
        AssertEx.Equal("", console.StandardOutput);
        AssertEx.True(console.StandardError.StartsWith("The option '--bogus' is not recognized.", StringComparison.Ordinal), console.StandardError);
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, payloadAlias: true).RunAsync(["noisy", "--json"]));
        AssertEx.Equal("", console.StandardOutput);
        AssertEx.Equal("Working\n", console.StandardError);
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, payloadAlias: true).RunAsync(["--version", "--json"]));
        AssertEx.Equal("1.2.3-ä\n", console.StandardOutput);
    }

    [Test, DisplayName("json-output/ascii-escaping-writes-pure-ascii")]
    public async ValueTask AsciiEscapingWritesPureAscii()
    {
        var ascii = new CommandJsonOutputOptions { Escaping = CommandJsonEscaping.Ascii, DocumentEveryOutcome = true };
        Encoding oem = CodePagesEncodingProvider.Instance.GetEncoding(850)!;
        foreach ((string[] args, bool payloadAlias) in new (string[], bool)[]
        {
            (["jo-rooms", "--format", "json"], false),
            (["jo-domain", "--data", "--format", "json"], false),
            (["jo-rooms", "--json"], true),
            (["jo-domain", "--data", "--json"], true),
            (["jo-domain", "--json"], true),
            (["--version", "--json"], true),
            (["--version", "--format", "json"], false),
        })
        {
            string label = string.Join(' ', args);
            var bytes = new ByteConsole();
            await App(bytes, ascii, payloadAlias).RunAsync(args);
            byte[] output = bytes.Output.ToArray();
            AssertEx.True(output.Length > 0 && output.All(static value => value < 0x80), label + ": " + Encoding.UTF8.GetString(output));
            AssertEx.True(Encoding.ASCII.GetString(output).Contains("\\u00E4", StringComparison.Ordinal), label + ": " + Encoding.ASCII.GetString(output));

            // Windows PowerShell 5.1 decodes a native program's output with the OEM code page; ASCII reads the same.
            AssertEx.Equal(Encoding.UTF8.GetString(output), oem.GetString(output), label);
            using JsonDocument document = JsonDocument.Parse(output);
        }

        var rooms = new TestCommandConsole();
        await App(rooms, ascii, payloadAlias: true).RunAsync(["jo-rooms", "--json"]);
        AssertEx.Equal("\"R\\u00E4ume \\uD83D\\uDE00\"\n", rooms.StandardOutput);
        using (JsonDocument document = JsonDocument.Parse(rooms.StandardOutput))
        {
            AssertEx.Equal(Rooms, document.RootElement.GetString());
        }

        var envelope = new TestCommandConsole();
        await App(envelope, ascii).RunAsync(["jo-domain", "--data", "--format", "json"]);
        CommandResponse<string> response = CommandJsonEnvelopeReader.Read(
            Encoding.ASCII.GetBytes(envelope.StandardOutput), CommandResultCodecs.String.PayloadType, CommandResultCodecs.String.TypeInfo);
        AssertEx.Equal("Die Räume sind gesperrt.", response.Fault!.Message);
        AssertEx.True(response.FailureData!.TryGet("tests.rooms/1", RecoveryJsonContext.Default.StringArray, out string[]? rows));
        AssertEx.Equal("Räume", rows!.Single());

        // The default keeps readable UTF-8, which a legacy code page decodes wrongly.
        var unicode = new ByteConsole();
        await App(unicode, payloadAlias: true).RunAsync(["jo-rooms", "--json"]);
        byte[] utf8 = unicode.Output.ToArray();
        AssertEx.Equal(RoomsJson, Encoding.UTF8.GetString(utf8));
        AssertEx.True(oem.GetString(utf8) != Encoding.UTF8.GetString(utf8));

        AssertEx.Throws<ArgumentOutOfRangeException>(() => new CommandJsonOutputOptions { Escaping = (CommandJsonEscaping)7 });
    }

    [Test, DisplayName("json-output/defaults-and-hosting")]
    public async ValueTask DefaultsAndHosting()
    {
        AssertEx.True(!CommandJsonOutputOptions.Default.DocumentEveryOutcome && !CommandJsonOutputOptions.Default.QuietStandardError);
        AssertEx.Equal(CommandJsonEscaping.Unicode, CommandJsonOutputOptions.Default.Escaping);
        AssertEx.True(ReferenceEquals(CommandJsonOutputOptions.Default, new CommandApp(GeneratedCommandCatalog.Create()).JsonOutput));
        AssertEx.True(ReferenceEquals(CommandJsonOutputOptions.Default, new CommandOutputDispatcher().JsonOutput));
        AssertEx.True(ReferenceEquals(CommandJsonOutputOptions.Default, new CommandPresentation().JsonOutput));
        AssertEx.Throws<ArgumentNullException>(() => new CommandApp(GeneratedCommandCatalog.Create()) { JsonOutput = null! });
        AssertEx.Throws<ArgumentNullException>(() => new CommandOutputDispatcher { JsonOutput = null! });
        AssertEx.Throws<ArgumentNullException>(() => new CommandPresentation { JsonOutput = null! });

        // The hosting adapter created from the app, and the tester, follow the app's options.
        var json = new CommandJsonOutputOptions { DocumentEveryOutcome = true, QuietStandardError = true, Escaping = CommandJsonEscaping.Ascii };
        foreach (string[] args in new[] { new[] { "jo-rooms", "--bogus", "--json" }, ["jo-domain", "--json"], ["noisy", "--json"] })
        {
            string label = string.Join(' ', args);
            var console = new TestCommandConsole();
            var hosted = new CommandLineHostingAdapter(App(console, json, payloadAlias: true));
            await hosted.RunAsync(hosted.Classify(new HostedCommandLineLaunchInput(args)));
            AssertEx.True(console.StandardOutput.Length > 0 && console.StandardOutput.All(static c => c < 0x80), label + ": " + console.StandardOutput);
            AssertEx.Equal("", console.StandardError, label);

            CommandAppTestResult tested = await new CommandAppTester(testConsole => App(testConsole, json, payloadAlias: true)).RunAsync(args);
            AssertEx.True(tested.StandardOutput.Length > 0 && tested.StandardOutput.All(static c => c < 0x80), label + ": " + tested.StandardOutput);
            AssertEx.Equal("", tested.StandardError, label);
        }
    }

    private sealed class ByteConsole : ICommandConsole
    {
        internal MemoryStream Output { get; } = new();

        public bool IsInteractive => false;
        public bool IsInputRedirected => true;
        public bool IsOutputRedirected => true;
        public bool IsErrorRedirected => true;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            Output.Write(Encoding.UTF8.GetBytes(value.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            Output.Write(value.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
