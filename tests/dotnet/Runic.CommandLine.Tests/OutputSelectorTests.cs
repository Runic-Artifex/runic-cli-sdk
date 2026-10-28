using Runic.CommandLine.Generated;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// The output-classification corpus covers the default selector; these cases cover a declared selector, its hidden
// aliases and the catalog validation around them.
internal sealed class OutputSelectorTests
{
    private static readonly CommandCatalog Legacy = FixtureCatalog.Create(static builder => builder.OutputSelector("--format", "--output"));

    [Test, DisplayName("output-selector/defaults-to-format")]
    public ValueTask DefaultsToFormat()
    {
        CommandCatalog catalog = FixtureCatalog.Create();
        AssertEx.Equal("--format", catalog.OutputSelector);
        AssertEx.Equal(0, catalog.OutputSelectorAliases.Count);
        AssertEx.Equal("--format", Legacy.OutputSelector);
        AssertEx.SequenceEqual(["--output"], Legacy.OutputSelectorAliases);

        // Without a declared alias, the former spelling is an unknown option.
        ParseOutcome legacy = Parse(catalog, "status", "--output", "json");
        AssertEx.Equal("RCLI1001", legacy.Diagnostics.Single().Code);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/hidden-alias-parses-like-the-selector")]
    public ValueTask HiddenAliasParsesLikeSelector()
    {
        foreach (string[] args in new[]
        {
            new[] { "status", "--output", "json" },
            ["status", "--output=JSON"],
            ["--output", "json", "status"],
            ["--output=json", "status", "-v"],
        })
        {
            ParseOutcome outcome = Parse(Legacy, args);
            AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind, string.Join(' ', args));
            AssertEx.Equal("status", outcome.Invocation!.Command.Name);
            AssertEx.Equal(CommandOutputMode.Json, outcome.Invocation.OutputClassification.Mode);
            AssertEx.Equal(CommandOutputModeSource.ExplicitArgument, outcome.Invocation.OutputClassification.Source);
        }

        // An explicit alias takes precedence over the environment, as the selector does.
        ParseOutcome human = PortableCommandSyntaxAdapter.Instance.Parse(Legacy, ["status", "--output", "human"], new ParseSettings("json"));
        AssertEx.Equal(CommandOutputMode.Human, human.Invocation!.OutputClassification.Mode);

        // Help and version are classified the same way.
        AssertEx.Equal(CommandOutputMode.Json, Parse(Legacy, "--output=json", "--help").OutputClassification!.Value.Mode);
        AssertEx.Equal(CommandOutputMode.Json, Parse(Legacy, "--version", "--output", "json").OutputClassification!.Value.Mode);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/hidden-alias-after-terminator-is-data")]
    public ValueTask HiddenAliasAfterTerminatorIsData()
    {
        ParseOutcome outcome = Parse(Legacy, "echo", "--", "--output=json", "--output", "json");
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind);
        AssertEx.Equal(CommandOutputMode.Human, outcome.Invocation!.OutputClassification.Mode);
        AssertEx.Equal(CommandOutputModeSource.Default, outcome.Invocation.OutputClassification.Source);
        AssertEx.SequenceEqual(["--output=json", "--output", "json"], outcome.Invocation.Arguments.Single().Values);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/alias-and-selector-conflict-and-repeats-are-duplicates")]
    public ValueTask AliasConflicts()
    {
        CommandDiagnostic conflict = Parse(Legacy, "status", "--output", "json", "--format=json").Diagnostics.Single();
        AssertEx.Equal("RCLI1016", conflict.Code);
        AssertEx.Equal("conflicting-output-selector", conflict.Kind);
        AssertEx.Equal(3, conflict.TokenIndex);
        AssertEx.SequenceEqual(["--format", "--output"], conflict.Arguments);

        CommandDiagnostic reversed = Parse(Legacy, "--format", "human", "status", "--output=human").Diagnostics.Single();
        AssertEx.Equal("RCLI1016", reversed.Code);
        AssertEx.SequenceEqual(["--output", "--format"], reversed.Arguments);

        CommandDiagnostic repeated = Parse(Legacy, "status", "--output", "json", "--output=json").Diagnostics.Single();
        AssertEx.Equal("RCLI1007", repeated.Code);
        AssertEx.Equal("duplicate-option", repeated.Kind);
        AssertEx.SequenceEqual(["--output"], repeated.Arguments);

        // With the JSON alias as well, every pair of different spellings conflicts.
        CommandCatalog withJson = FixtureCatalog.Create(static builder => builder.OutputSelector("--format", "--output", "--mode"));
        AssertEx.Equal("--json", withJson.JsonOutputAlias);
        AssertEx.Equal("RCLI1016", Parse(withJson, "status", "--json", "--output=json").Diagnostics.Single().Code);
        AssertEx.SequenceEqual(["--mode", "--output"], Parse(withJson, "status", "--output=json", "--mode=json").Diagnostics.Single().Arguments);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/hidden-alias-diagnostics-name-the-selector")]
    public ValueTask DiagnosticsNameSelector()
    {
        CommandDiagnostic invalid = Parse(Legacy, "status", "--output", "xml").Diagnostics.Single();
        AssertEx.Equal("RCLI1010", invalid.Code);
        AssertEx.SequenceEqual(["--format"], invalid.Arguments);
        AssertEx.True(!invalid.Message.Contains("xml", StringComparison.Ordinal), invalid.Message);

        CommandDiagnostic inline = Parse(Legacy, "--output=TOPSECRET").Diagnostics.Single();
        AssertEx.Equal("RCLI1010", inline.Code);
        AssertEx.SequenceEqual(["--format"], inline.Arguments);
        AssertEx.True(!inline.Message.Contains("TOPSECRET", StringComparison.Ordinal), inline.Message);

        CommandDiagnostic missing = Parse(Legacy, "status", "--output").Diagnostics.Single();
        AssertEx.Equal("RCLI1003", missing.Code);
        AssertEx.SequenceEqual(["--format"], missing.Arguments);

        // A typo of the alias is not corrected to the alias, which help never shows.
        CommandDiagnostic typo = Parse(Legacy, "status", "--outptu").Diagnostics.Single();
        AssertEx.Equal("RCLI1001", typo.Code);
        AssertEx.True(!typo.Message.Contains("--output", StringComparison.Ordinal), typo.Message);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/help-and-completion-omit-hidden-aliases")]
    public ValueTask HelpAndCompletionOmitAliases()
    {
        CommandCatalog plain = FixtureCatalog.Create();
        foreach (CommandPath path in new[] { CommandPath.Root, new CommandPath(["status"]) })
        {
            string help = CommandHelpFormatter.Format(Legacy, "fixture", path);
            AssertEx.True(help.Contains("--format <human|json>", StringComparison.Ordinal), help);
            AssertEx.True(help.Contains("Same as --format json", StringComparison.Ordinal), help);
            AssertEx.True(!help.Contains("--output", StringComparison.Ordinal), help);
            AssertEx.Equal(CommandHelpFormatter.Format(plain, "fixture", path), help);
        }

        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            string script = CommandCompletion.Generate(Legacy, "fixture", shell);
            AssertEx.True(!script.Contains("--output", StringComparison.Ordinal), shell);
            AssertEx.Equal(CommandCompletion.Generate(plain, "fixture", shell), script, shell);
        }

        foreach (string[] words in new[]
        {
            new[] { "" }, ["--"], ["--o"], ["status", "--"], ["status", "--format", ""], ["status", "--output", ""], ["status", "--output=", ""],
        })
        {
            CommandCompletionResult expected = CommandCompletion.Query(plain, words);
            CommandCompletionResult actual = CommandCompletion.Query(Legacy, words);
            AssertEx.SequenceEqual(expected.Candidates, actual.Candidates, string.Join(' ', words));
            AssertEx.True(!actual.Candidates.Contains("--output"), string.Join(' ', words));
        }

        AssertEx.True(CommandCompletion.Query(Legacy, ["status", "--"]).Candidates.Contains("--format"));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/command-app-writes-json-through-the-alias")]
    public async ValueTask CommandAppUsesAlias()
    {
        var console = new TestCommandConsole();
        var app = new CommandApp(GeneratedCommandCatalog.Create(static builder => builder.OutputSelector("--format", "--output")))
        {
            Name = "sample", Console = console, HandleTerminationSignals = false, ParseSettings = ParseSettings.Default,
        };
        AssertEx.Equal(0, await app.RunAsync(["--output", "json", "greet", "Ada"]));
        using (CommandTestEnvelope.Parse(console.StandardOutput))
        {
        }

        console = new TestCommandConsole();
        app = new CommandApp(GeneratedCommandCatalog.Create(static builder => builder.OutputSelector("--format", "--output")))
        {
            Name = "sample", Console = console, HandleTerminationSignals = false, ParseSettings = ParseSettings.Default,
        };
        AssertEx.Equal(2, await app.RunAsync(["greet", "Ada", "--output", "yaml"]));
        AssertEx.Equal("--format must be human or json.\nUsage: sample <command> [options]\nRun \"sample --help\" for more information.\n", console.StandardError);
    }

    [Test, DisplayName("output-selector/builder-validates-spellings")]
    public ValueTask BuilderValidatesSpellings()
    {
        foreach (string invalid in new[] { "format", "-f", "--Format", "--help", "--version", "--" })
        {
            AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().OutputSelector(invalid));
            AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().OutputSelector("--format", invalid));
        }

        AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().OutputSelector("--format", "--format"));
        AssertEx.Throws<ArgumentException>(() => new CommandCatalogBuilder().OutputSelector("--format", "--output", "--output"));
        AssertEx.Throws<ArgumentNullException>(() => new CommandCatalogBuilder().OutputSelector(null!));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/option-spelled-like-the-selector-is-rcli0026")]
    public ValueTask OptionSpelledLikeSelector()
    {
        // The default selector.
        var byDefault = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(byDefault, "report").Subcommand<TestOptions, TestHandler, TestResult>("list", list =>
            list.BindWith(new TestBinder()).CreateHandlerWith(new TestHandlerFactory()).Produces(new TestCodec())
                .Option("format", "--format", CommandArity.ExactlyOne));
        CommandCatalogIssue issue = AssertEx.Throws<CommandCatalogValidationException>(byDefault.Build).Issues.Single();
        AssertEx.Equal("RCLI0026", issue.Code);
        AssertEx.Equal("report list", issue.Location);
        AssertEx.True(issue.Message.Contains("'--format'", StringComparison.Ordinal), issue.Message);

        // An option alias spelled like a hidden alias.
        var byAlias = new CommandCatalogBuilder().OutputSelector("--format", "--output");
        CatalogTests.ValidCommand(byAlias, "report").Option("target", "--target", CommandArity.ExactlyOne, aliases: ["--output"]);
        issue = AssertEx.Throws<CommandCatalogValidationException>(byAlias.Build).Issues.Single();
        AssertEx.Equal("RCLI0026", issue.Code);
        AssertEx.Equal("report", issue.Location);
        AssertEx.True(issue.Message.Contains("alias '--output'", StringComparison.Ordinal), issue.Message);

        // A declared selector frees the default spelling for the application.
        var declared = new CommandCatalogBuilder().OutputSelector("--output");
        CatalogTests.ValidCommand(declared, "report").Option("format", "--format", CommandArity.ExactlyOne);
        CommandCatalog catalog = declared.Build();
        ParseOutcome parsed = Parse(catalog, "report", "--format", "csv", "--output=json");
        AssertEx.Equal(ParseOutcomeKind.Invocation, parsed.Kind);
        AssertEx.Equal("csv", parsed.Invocation!.Options.Single().Values.Single());
        AssertEx.Equal(CommandOutputMode.Json, parsed.Invocation.OutputClassification.Mode);
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("output-selector/combine-requires-one-selector")]
    public ValueTask CombineRequiresOneSelector()
    {
        CommandCatalog runic = Single("first", static builder => builder.OutputSelector("--runic-output"));
        CommandCatalog legacy = Single("second", static builder => builder.OutputSelector("--format", "--output"));
        CommandCatalog undeclared = Single("third", null);

        CommandCatalogIssue issue = AssertEx.Throws<CommandCatalogValidationException>(() => CommandCatalog.Combine(runic, legacy)).Issues.Single();
        AssertEx.Equal("RCLI0026", issue.Code);
        AssertEx.Equal("root", issue.Location);

        // Different hidden aliases are a different selector.
        CommandCatalog fewerAliases = Single("fourth", static builder => builder.OutputSelector("--format"));
        AssertEx.Equal("RCLI0026", AssertEx.Throws<CommandCatalogValidationException>(
            () => CommandCatalog.Combine(legacy, fewerAliases)).Issues.Single().Code);

        // A catalog that declares none takes the declared selector, in either order.
        foreach (CommandCatalog combined in new[] { CommandCatalog.Combine(undeclared, legacy), CommandCatalog.Combine(legacy, undeclared) })
        {
            AssertEx.Equal("--format", combined.OutputSelector);
            AssertEx.SequenceEqual(["--output"], combined.OutputSelectorAliases);
            AssertEx.Equal(2, combined.Commands.Count);
        }

        // The same declaration, aliases in another order, is the same selector.
        CommandCatalog same = CommandCatalog.Combine(
            Single("a", static builder => builder.OutputSelector("--format", "--output", "--mode")),
            Single("b", static builder => builder.OutputSelector("--format", "--mode", "--output")));
        AssertEx.Equal("--format", same.OutputSelector);

        // The declared selector applies to the options of the catalogs that did not declare it.
        var option = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(option, "fifth").Option("transport", "--runic-output", CommandArity.ExactlyOne);
        CommandCatalog withOption = option.Build();
        issue = AssertEx.Throws<CommandCatalogValidationException>(() => CommandCatalog.Combine(runic, withOption)).Issues.Single();
        AssertEx.Equal("RCLI0026", issue.Code);
        AssertEx.Equal("fifth", issue.Location);
        return ValueTask.CompletedTask;
    }

    private static CommandCatalog Single(string name, Action<CommandCatalogBuilder>? configure)
    {
        var builder = new CommandCatalogBuilder();
        configure?.Invoke(builder);
        CatalogTests.ValidCommand(builder, name);
        return builder.Build();
    }

    private static ParseOutcome Parse(CommandCatalog catalog, params string[] args) =>
        PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, ParseSettings.Default);
}
