using Runic.CommandLine.Generated;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// The grammar and output-classification corpora cover the alias's parsing; these cases cover what surrounds it.
internal sealed class JsonOutputAliasTests
{
    private static readonly string[] JsonCompletion = ["--json"];

    [Test, DisplayName("output-alias/command-app-writes-json-and-diagnoses-the-selector")]
    public async ValueTask CommandAppSelectsJson()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["greet", "--json"]));
        using (CommandTestEnvelope.Parse(console.StandardOutput))
        {
        }

        console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["greet", "--json", "--output", "human"]));
        // The selection is frozen at the first selector, so the usage error is a JSON envelope.
        AssertEx.True(console.StandardOutput.Contains("RCLI1016", StringComparison.Ordinal) &&
            console.StandardOutput.Contains("--output and --json both select the output format", StringComparison.Ordinal),
            console.StandardOutput);

        console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["greet", "--output", "human", "--json"]));
        AssertEx.True(
            console.StandardError.Contains("--json and --output both select the output format", StringComparison.Ordinal),
            console.StandardError);
    }

    [Test, DisplayName("output-alias/help-and-completions-list-the-alias")]
    public ValueTask HelpAndCompletionsListAlias()
    {
        CommandCatalog catalog = FixtureCatalog.Create();
        AssertEx.Equal("--json", catalog.JsonOutputAlias);

        string help = CommandHelpFormatter.Format(catalog, "fixture", new CommandPath(["status"]));
        AssertEx.True(help.Contains("--output <human|json>", StringComparison.Ordinal), help);
        AssertEx.True(help.Contains("--json", StringComparison.Ordinal) &&
            help.Contains("Same as --output json", StringComparison.Ordinal), help);
        string hostedHelp = CommandHelpFormatter.Format(catalog, "fixture", CommandPath.Root, "--runic-output");
        AssertEx.True(hostedHelp.Contains("Same as --runic-output json", StringComparison.Ordinal), hostedHelp);

        AssertEx.SequenceEqual(JsonCompletion, CommandCompletion.Query(catalog, ["status", "--js"]).Candidates);
        AssertEx.SequenceEqual(JsonCompletion, CommandCompletion.Query(catalog, ["--js"]).Candidates);
        // The alias takes no value, so the next word completes options again.
        AssertEx.True(CommandCompletion.Query(catalog, ["status", "--json", "--"]).Candidates.Contains("--verbose"));
        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            AssertEx.True(CommandCompletion.Generate(catalog, "fixture", shell).Contains("--json", StringComparison.Ordinal), shell);
        }

        ParseOutcome typo = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["status", "--jsn"], ParseSettings.Default);
        AssertEx.True(typo.Diagnostics.Single().Message.Contains("Did you mean '--json'?", StringComparison.Ordinal));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-alias/maps-to-a-hosted-transport-option")]
    public ValueTask MapsToHostedTransport()
    {
        CommandCatalog catalog = FixtureCatalog.Create();
        var hosted = new ParseSettings(transportOutputOptionName: "--runic-output");
        ParseOutcome json = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["status", "--json"], hosted);
        AssertEx.Equal(CommandOutputMode.Json, json.Invocation!.OutputClassification.Mode);

        ParseOutcome conflict = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog, ["status", "--json", "--runic-output=human"], hosted);
        CommandDiagnostic diagnostic = conflict.Diagnostics.Single();
        AssertEx.Equal("RCLI1016", diagnostic.Code);
        AssertEx.SequenceEqual(["--runic-output", "--json"], diagnostic.Arguments);
        AssertEx.Equal(CommandTextKeys.DiagnosticConflictingOutputSelector, CommandTextKeys.Diagnostic(diagnostic.Kind));

        // A selector spelled like the alias is the selector, with its value.
        ParseOutcome same = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog, ["status", "--json", "human"], new ParseSettings(transportOutputOptionName: "--json"));
        AssertEx.Equal(CommandOutputMode.Human, same.Invocation!.OutputClassification.Mode);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-alias/catalog-validates-the-alias")]
    public ValueTask CatalogValidatesAlias()
    {
        foreach (string invalid in new[] { "json", "-j", "--Json", "--help", "--version", "--" })
        {
            AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().JsonOutputAlias(invalid));
        }

        var colliding = new CommandCatalogBuilder().JsonOutputAlias("--json");
        CatalogTests.ValidCommand(colliding, "report").Subcommand<TestOptions, TestHandler, TestResult>("list", list =>
            list.BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec())
                .Option("json", "--as-json", CommandArity.Zero, CommandOptionRepeatPolicy.Error, false, null, "--json"));
        CommandCatalogValidationException collision = AssertEx.Throws<CommandCatalogValidationException>(colliding.Build);
        CommandCatalogIssue issue = collision.Issues.Single();
        AssertEx.Equal("RCLI0024", issue.Code);
        AssertEx.Equal("report list", issue.Location);

        CommandCatalog first = Single("first", "--json");
        CommandCatalog combined = CommandCatalog.Combine(first, Single("second", null));
        AssertEx.Equal("--json", combined.JsonOutputAlias);
        AssertEx.Equal("RCLI0024", AssertEx.Throws<CommandCatalogValidationException>(
            () => CommandCatalog.Combine(first, Single("third", "--as-json"))).Issues.Single().Code);
        return ValueTask.CompletedTask;
    }

    private static CommandCatalog Single(string name, string? alias)
    {
        var builder = new CommandCatalogBuilder();
        if (alias is not null)
        {
            builder.JsonOutputAlias(alias);
        }

        CatalogTests.ValidCommand(builder, name);
        return builder.Build();
    }

    private static CommandApp App(ICommandConsole console) =>
        new(GeneratedCommandCatalog.Create(builder => builder.JsonOutputAlias("--json")))
        {
            Console = console,
            ParseSettings = ParseSettings.Default,
            HandleCancelKeyPress = false,
        };
}
