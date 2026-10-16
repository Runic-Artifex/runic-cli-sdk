using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// The unnamed root command of a single-command application: runic-cli-sdk#43.
internal sealed class RootCommandTests
{
    private const string Commands = """
        using Runic.CommandLine;

        internal static class Commands
        {
            [Command("run", Description = "Manage the machine.", Examples = ["fixture home switch"])]
            [RootCommand]
            public static string Run(
                [Argument(AllowMultipleValues = true, Choices = ["home", "system", "switch"], ValueName = "WORD")] string[]? words,
                [Option("--dry-run", Description = "Preview only")] bool dryRun = false) =>
                "run:" + string.Join(",", words ?? []) + ":" + dryRun;

            [Command("software list", Description = "List software.")]
            public static string List() => "software";
        }
        """;

    [Test, DisplayName("root-command/receives-command-lines-without-its-name")]
    public async ValueTask Dispatch()
    {
        await ArgumentTests.WithCatalog(Commands, async catalog =>
        {
            AssertEx.Equal("run", catalog.RootCommand!.Name);
            AssertEx.True(ReferenceEquals(catalog.RootCommand, catalog.DefaultCommand));
            AssertEx.True(!catalog.TryGetCommand("run", out _));
            AssertEx.Equal("run:home,switch:True", await ArgumentTests.Run(catalog, 0, "--dry-run", "home", "switch"));
            AssertEx.Equal("run::False", await ArgumentTests.Run(catalog, 0));
            AssertEx.Equal("software", await ArgumentTests.Run(catalog, 0, "software", "list"));
            // The name is not a spelling, so it is an argument that fails the choices.
            string error = await ArgumentTests.Run(catalog, CommandExitCodes.Usage, "run", "home");
            AssertEx.True(error.Contains("RCLI1015", StringComparison.Ordinal) || error.Contains("WORD", StringComparison.Ordinal), error);

            // JSON output keeps the name as the command's identity.
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await new CommandApp(catalog) { Name = "fixture", Console = console, ParseSettings = ParseSettings.Default, HandleCancelKeyPress = false }
                .RunAsync(["home", "--output", "json"]));
            using var envelope = CommandTestEnvelope.Parse(console.StandardOutput);
            AssertEx.Equal("run", envelope.RootElement.GetProperty("command").GetString());
        });
    }

    private static readonly string[][] HelpLines = [["--help"], ["help"], ["-h"], ["home", "--help"]];
    private static readonly string[] HelpContent = ["Manage the machine.", "WORD", "--dry-run", "Preview only", "software", "fixture home switch"];

    [Test, DisplayName("root-command/application-help-is-its-help")]
    public async ValueTask Help()
    {
        await ArgumentTests.WithCatalog(Commands, async catalog =>
        {
            foreach (string[] args in HelpLines)
            {
                string help = await ArgumentTests.Run(catalog, 0, args);
                AssertEx.True(help.StartsWith("Usage: fixture [command] [WORD...] [options]\n", StringComparison.Ordinal), string.Join(' ', args) + ": " + help);
                foreach (string expected in HelpContent)
                    AssertEx.True(help.Contains(expected, StringComparison.Ordinal), expected + ": " + help);
                AssertEx.True(!help.Contains("\n  run", StringComparison.Ordinal) && !help.Contains("fixture run", StringComparison.Ordinal), help);
            }

            string group = await ArgumentTests.Run(catalog, 0, "help", "software");
            AssertEx.True(group.Contains("Usage: fixture software", StringComparison.Ordinal) && !group.Contains("--dry-run", StringComparison.Ordinal), group);

            AssertEx.SequenceEqual(["software"], CommandCompletion.Query(catalog, ["so"]).Candidates);
            AssertEx.SequenceEqual(Array.Empty<string>(), CommandCompletion.Query(catalog, ["ru"]).Candidates);
            AssertEx.SequenceEqual(["home"], CommandCompletion.Query(catalog, ["ho"]).Candidates);
            AssertEx.SequenceEqual(["--dry-run"], CommandCompletion.Query(catalog, ["home", "--dr"]).Candidates);
        });
    }

    [Test, DisplayName("root-command/catalog-validation-and-combination")]
    public ValueTask Catalog()
    {
        var aliased = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(aliased, "run").Alias("go");
        AssertEx.Equal("RCLI0018", AssertEx.Throws<CommandCatalogValidationException>(aliased.RootCommand("run").Build).Issues.Single().Code);
        var missing = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(missing, "run");
        AssertEx.Equal("RCLI0018", AssertEx.Throws<CommandCatalogValidationException>(missing.RootCommand("walk").Build).Issues.Single().Code);

        var root = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(root, "run");
        CommandCatalog first = root.RootCommand("run").Build();
        var other = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(other, "status");
        CommandCatalog combined = CommandCatalog.Combine(other.Build(), first);
        AssertEx.Equal("run", combined.RootCommand!.Name);
        AssertEx.True(!combined.TryGetCommand("run", out _) && combined.TryGetCommand("status", out _));

        // DefaultCommand after RootCommand selects a named default command again.
        var named = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(named, "run");
        CommandCatalog defaulted = named.RootCommand("run").DefaultCommand("run").Build();
        AssertEx.True(defaulted.RootCommand is null && defaulted.TryGetCommand("run", out _));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("root-command/generator-reports-several-default-commands")]
    public ValueTask GeneratorDiagnostics()
    {
        ArgumentTests.CSharpCompilationDiagnostics("""
            using Runic.CommandLine;

            internal static class Commands
            {
                [Command("first"), RootCommand]
                public static string First() => "";

                [Command("second"), DefaultCommand]
                public static string Second() => "";

                [Command("nested one"), RootCommand]
                public static string Nested() => "";
            }
            """, ["RCLI9040", "RCLI9029", "RCLI9029"]);
        return ValueTask.CompletedTask;
    }
}
