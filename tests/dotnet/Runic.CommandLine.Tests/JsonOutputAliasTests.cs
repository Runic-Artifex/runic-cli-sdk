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
        AssertEx.Equal(2, await App(console).RunAsync(["greet", "--json", "--format", "human"]));
        // The selection is frozen at the first selector, so the usage error is a JSON envelope.
        AssertEx.True(console.StandardOutput.Contains("RCLI1016", StringComparison.Ordinal) &&
            console.StandardOutput.Contains("--format and --json both select the output format", StringComparison.Ordinal),
            console.StandardOutput);

        console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["greet", "--format", "human", "--json"]));
        AssertEx.True(
            console.StandardError.Contains("--json and --format both select the output format", StringComparison.Ordinal),
            console.StandardError);
    }

    [Test, DisplayName("output-alias/help-and-completions-list-the-alias")]
    public ValueTask HelpAndCompletionsListAlias()
    {
        CommandCatalog catalog = FixtureCatalog.Create();
        AssertEx.Equal("--json", catalog.JsonOutputAlias);

        string help = CommandHelpFormatter.Format(catalog, "fixture", new CommandPath(["status"]));
        AssertEx.True(help.Contains("--format <human|json>", StringComparison.Ordinal), help);
        AssertEx.True(help.Contains("--json", StringComparison.Ordinal) &&
            help.Contains("Same as --format json", StringComparison.Ordinal), help);
        string hostedHelp = CommandHelpFormatter.Format(
            FixtureCatalog.Create(static builder => builder.OutputSelector("--runic-output")), "fixture", CommandPath.Root);
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
        CommandCatalog catalog = FixtureCatalog.Create(static builder => builder.OutputSelector("--runic-output"));
        ParseSettings hosted = ParseSettings.Default;
        ParseOutcome json = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["status", "--json"], hosted);
        AssertEx.Equal(CommandOutputMode.Json, json.Invocation!.OutputClassification.Mode);

        ParseOutcome conflict = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog, ["status", "--json", "--runic-output=human"], hosted);
        CommandDiagnostic diagnostic = conflict.Diagnostics.Single();
        AssertEx.Equal("RCLI1016", diagnostic.Code);
        AssertEx.SequenceEqual(["--runic-output", "--json"], diagnostic.Arguments);
        AssertEx.Equal(CommandTextKeys.DiagnosticConflictingOutputSelector, CommandTextKeys.Diagnostic(diagnostic.Kind));

        // A selector spelled like the alias is rejected when the catalog is built.
        AssertEx.Equal("RCLI0024", AssertEx.Throws<CommandCatalogValidationException>(
            () => FixtureCatalog.Create(static builder => builder.OutputSelector("--json"))).Issues.Single().Code);
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
                .Option("json", "--as-json", CommandArity.Zero, aliases: ["--json"]));
        CommandCatalogValidationException collision = AssertEx.Throws<CommandCatalogValidationException>(colliding.Build);
        CommandCatalogIssue issue = collision.Issues.Single();
        AssertEx.Equal("RCLI0024", issue.Code);
        AssertEx.Equal("report list", issue.Location);

        // The alias may not be spelled like the selector (the default --format, a declared one) or a hidden selector alias.
        foreach ((string alias, string[] selectorSpellings) in new (string, string[])[]
        {
            ("--format", []), ("--runic-output", ["--runic-output"]), ("--output", ["--format", "--output"]),
        })
        {
            var selector = new CommandCatalogBuilder().JsonOutputAlias(alias);
            if (selectorSpellings.Length != 0) selector.OutputSelector(selectorSpellings[0], selectorSpellings[1..]);
            CatalogTests.ValidCommand(selector, "report");
            CommandCatalogIssue selectorIssue = AssertEx.Throws<CommandCatalogValidationException>(selector.Build).Issues.Single();
            AssertEx.Equal("RCLI0024", selectorIssue.Code);
            AssertEx.Equal("root", selectorIssue.Location);
        }

        // --output is an ordinary spelling for the alias under the default selector.
        var legacy = new CommandCatalogBuilder().JsonOutputAlias("--output");
        CatalogTests.ValidCommand(legacy, "report");
        AssertEx.Equal("--output", legacy.Build().JsonOutputAlias);

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
            HandleTerminationSignals = false,
        };
}
