using System.Text.Json;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// A JSON output alias of payload shape: the bare document on stdout, human text for everything else.
internal sealed class JsonPayloadOutputTests
{
    [Test, DisplayName("output-alias-payload/success-writes-the-bare-payload")]
    public async ValueTask SuccessWritesPayload()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["report", "--json"]));
        AssertEx.Equal("{\"status\":\"Ready\",\"count\":2}\n", console.StandardOutput);
        AssertEx.Equal("", console.StandardError);

        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["--json", "greet", "Ada"]));
        AssertEx.Equal("\"Hello Ada\"\n", console.StandardOutput);

        // A command without a result writes no document; its own output still stays off stdout.
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["noisy", "--json"]));
        AssertEx.Equal("", console.StandardOutput);
        AssertEx.Equal("Working\n", console.StandardError);
    }

    [Test, DisplayName("output-alias-payload/failure-writes-the-failure-data-and-a-human-fault")]
    public async ValueTask FailureWritesFailureData()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Cancelled, await App(console).RunAsync(["typed-recovery", "--json"]));
        AssertEx.True(console.StandardOutput.EndsWith("}\n", StringComparison.Ordinal) && console.StandardOutput.Count(c => c == '\n') == 1,
            console.StandardOutput);
        using (JsonDocument document = JsonDocument.Parse(console.StandardOutput))
        {
            AssertEx.Equal(FailureDataTests.ExactPath, document.RootElement.GetProperty("retainedDirectory").GetString());
            AssertEx.True(!document.RootElement.TryGetProperty("protocol", out _));
        }

        // The command's human failure text replaces the fault line on stderr.
        AssertEx.Equal("Inspect the retained directory.\n", console.StandardError);

        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.HostFailure, await App(console).RunAsync(["fail", "--json"]));
        AssertEx.Equal("", console.StandardOutput);
        AssertEx.True(console.StandardError.StartsWith("RCLI5000: ", StringComparison.Ordinal) &&
            !console.StandardError.Contains("internal-secret", StringComparison.Ordinal), console.StandardError);
    }

    [Test, DisplayName("output-alias-payload/failure-text-replaces-the-fault-line")]
    public async ValueTask FailureTextReplacesFaultLine()
    {
        // Without failure text: the document, then diagnostics and the CODE: message line.
        var console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.CommandFailure, await App(console).RunAsync(["payload-failure", "--json"]));
        AssertEx.Equal("[\"left\"]\n", console.StandardOutput);
        AssertEx.Equal("RCLI3999: Some items were skipped.\nTEST0001: The operation failed.\n", console.StandardError);

        // With failure text: the diagnostics stay, the text replaces the fault line and is written exactly.
        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.CommandFailure, await App(console).RunAsync(["payload-failure", "--human-text", "--json"]));
        AssertEx.Equal("[\"left\"]\n", console.StandardOutput);
        AssertEx.Equal("RCLI3999: Some items were skipped.\nError: the operation failed.\n", console.StandardError);

        // Human mode keeps writing the failure text to stdout and the fault line to stderr.
        console = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.CommandFailure, await App(console).RunAsync(["payload-failure", "--human-text"]));
        AssertEx.Equal("Error: the operation failed.\n", console.StandardOutput);
        AssertEx.True(console.StandardError.Contains("TEST0001: The operation failed.", StringComparison.Ordinal), console.StandardError);
    }

    private static readonly string[] LeftItems = ["left"];

    [Command("payload-failure")]
    internal static CommandOutcome<string> PayloadFailure([Option("--human-text")] bool humanText = false) =>
        CommandOutcome.Failure<string>(
            CommandExitCategory.CommandFailure,
            new CommandFault("TEST0001", "The operation failed."),
            [new CommandDiagnostic("RCLI3999", "skipped", "Some items were skipped.", CommandDiagnosticPhase.Execution, CommandDiagnosticSeverity.Warning)],
            humanText ? "Error: the operation failed.\n" : null,
            CommandFailureData.Create("tests.payload-failure/1", LeftItems, RecoveryJsonContext.Default.StringArray));

    [Test, DisplayName("output-alias-payload/framework-output-stays-human")]
    public async ValueTask FrameworkOutputStaysHuman()
    {
        foreach (string[] args in new[]
        {
            new[] { "greet", "--bogus", "--json" },
            ["--json", "greet", "--bogus"],
            ["greet", "--json", "--json"],
            ["greet", "--json", "--format", "json"],
            ["greet", "--json=yes"],
        })
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(CommandExitCodes.Usage, await App(console).RunAsync(args), string.Join(' ', args));
            AssertEx.Equal("", console.StandardOutput, string.Join(' ', args));
            AssertEx.True(console.StandardError.StartsWith("RCLI10", StringComparison.Ordinal), console.StandardError);
        }

        var help = new TestCommandConsole();
        AssertEx.Equal(0, await App(help).RunAsync(["greet", "--help", "--json"]));
        AssertEx.True(help.StandardOutput.StartsWith("Usage", StringComparison.Ordinal) &&
            help.StandardOutput.Contains("Write the result as JSON", StringComparison.Ordinal), help.StandardOutput);

        var version = new TestCommandConsole();
        AssertEx.Equal(0, await App(version).RunAsync(["--version", "--json"]));
        AssertEx.Equal("1.2.3\n", version.StandardOutput);

        // The same framework request selected without the alias keeps the envelope.
        var envelope = new TestCommandConsole();
        AssertEx.Equal(CommandExitCodes.Usage, await App(envelope).RunAsync(["greet", "--bogus", "--format", "json"]));
        using (CommandTestEnvelope.Parse(envelope.StandardOutput))
        {
        }
    }

    [Test, DisplayName("output-alias-payload/selector-and-environment-keep-the-envelope")]
    public async ValueTask SelectorAndEnvironmentKeepEnvelope()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["report", "--format", "json"]));
        using (JsonDocument document = CommandTestEnvelope.Parse(console.StandardOutput))
        {
            AssertEx.Equal("Ready", document.RootElement.GetProperty("payload").GetProperty("status").GetString());
        }

        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, new ParseSettings("json")).RunAsync(["report"]));
        using (CommandTestEnvelope.Parse(console.StandardOutput))
        {
        }

        // The alias is explicit, so it overrides the environment and writes the payload.
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, new ParseSettings("human")).RunAsync(["report", "--json"]));
        AssertEx.Equal("{\"status\":\"Ready\",\"count\":2}\n", console.StandardOutput);
    }

    [Test, DisplayName("output-alias-payload/shape-reaches-classification-context-and-hosting")]
    public async ValueTask ShapeReachesContextAndHosting()
    {
        CommandCatalog catalog = Catalog();
        AssertEx.Equal(JsonOutputShape.Payload, catalog.JsonOutputAliasShape);
        ParseOutcome alias = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["report", "--json"], ParseSettings.Default);
        AssertEx.Equal(CommandOutputMode.Json, alias.Invocation!.OutputClassification.Mode);
        AssertEx.Equal(JsonOutputShape.Payload, alias.Invocation.OutputClassification.Shape);
        ParseOutcome selector = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["report", "--format=json"], ParseSettings.Default);
        AssertEx.Equal(JsonOutputShape.Envelope, selector.Invocation!.OutputClassification.Shape);
        ParseOutcome envelopeAlias = PortableCommandSyntaxAdapter.Instance.Parse(
            GeneratedCommandCatalog.Create(builder => builder.JsonOutputAlias("--json")), ["report", "--json"], ParseSettings.Default);
        AssertEx.Equal(JsonOutputShape.Envelope, envelopeAlias.Invocation!.OutputClassification.Shape);

        var sink = new ShapeSink();
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await new CommandApp(catalog) { Console = console, OutcomeSink = sink, ParseSettings = ParseSettings.Default, HandleTerminationSignals = false }
            .RunAsync(["report", "--json"]));
        AssertEx.Equal(JsonOutputShape.Payload, sink.Shape);

        var hosted = new CommandLineHostingAdapter(App(console = new TestCommandConsole()));
        HostedCommandLineDecision decision = hosted.Classify(new HostedCommandLineLaunchInput(["report", "--json"]));
        AssertEx.Equal(0, await hosted.RunAsync(decision));
        AssertEx.Equal("{\"status\":\"Ready\",\"count\":2}\n", console.StandardOutput);
        decision = hosted.Classify(new HostedCommandLineLaunchInput(["report", "--bogus", "--json"]));
        AssertEx.Equal(CommandExitCodes.Usage, await hosted.RunAsync(decision));
        AssertEx.True(console.StandardError.StartsWith("RCLI1001", StringComparison.Ordinal), console.StandardError);
    }

    [Test, DisplayName("output-alias-payload/catalog-validates-the-shape")]
    public ValueTask CatalogValidatesShape()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => new CommandCatalogBuilder().JsonOutputAlias("--json", (JsonOutputShape)2));
        AssertEx.Equal(JsonOutputShape.Envelope, Single("plain", "--json", JsonOutputShape.Envelope).JsonOutputAliasShape);

        CommandCatalog payload = Single("first", "--json", JsonOutputShape.Payload);
        CommandCatalog combined = CommandCatalog.Combine(payload, Single("second", null, JsonOutputShape.Envelope));
        AssertEx.Equal(JsonOutputShape.Payload, combined.JsonOutputAliasShape);
        AssertEx.Equal(JsonOutputShape.Payload,
            CommandCatalog.Combine(Single("third", null, JsonOutputShape.Envelope), payload).JsonOutputAliasShape);
        CommandCatalogIssue issue = AssertEx.Throws<CommandCatalogValidationException>(
            () => CommandCatalog.Combine(payload, Single("fourth", "--json", JsonOutputShape.Envelope))).Issues.Single();
        AssertEx.Equal("RCLI0024", issue.Code);
        return ValueTask.CompletedTask;
    }

    private static CommandCatalog Single(string name, string? alias, JsonOutputShape shape)
    {
        var builder = new CommandCatalogBuilder();
        if (alias is not null)
        {
            builder.JsonOutputAlias(alias, shape);
        }

        CatalogTests.ValidCommand(builder, name);
        return builder.Build();
    }

    private static CommandCatalog Catalog() =>
        GeneratedCommandCatalog.Create(builder => builder.JsonOutputAlias("--json", JsonOutputShape.Payload));

    private static CommandApp App(ICommandConsole console, ParseSettings? settings = null) =>
        new(Catalog())
        {
            Console = console,
            Version = "1.2.3",
            ParseSettings = settings ?? ParseSettings.Default,
            HandleTerminationSignals = false,
        };

    private sealed class ShapeSink : ICommandOutcomeSink
    {
        internal JsonOutputShape? Shape { get; private set; }

        public ValueTask WriteAsync<TResult>(CommandDescriptor command, CommandExecutionContext context, CommandOutcome<TResult> outcome,
            ICommandResultCodec<TResult> codec, int exitCode, IReadOnlyList<CommandDiagnostic> diagnostics, CancellationToken cancellationToken)
        {
            Shape = context.OutputShape;
            return ValueTask.CompletedTask;
        }
    }
}
