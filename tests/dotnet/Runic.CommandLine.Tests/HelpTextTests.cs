using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// Help and usage texts as users read them: extended help through a text key (#97), arguments and options named as the
// usage line shows them (#98), and no default for flags (#99).
internal sealed class HelpTextTests
{
    private static CommandApp App(TestCommandConsole console, ICommandTextResolver? resolver = null) =>
        new(GeneratedCommandCatalog.Create())
        {
            Name = "tool", Console = console, HandleTerminationSignals = false, TextResolver = resolver,
            Culture = CultureInfo.GetCultureInfo("de"), ParseSettings = ParseSettings.Default,
        };

    [Test, DisplayName("help-texts/long-description-key-resolves-with-its-line-breaks")]
    public async ValueTask LongDescriptionKeyResolves()
    {
        var resolver = new TextResolver(new Dictionary<string, string>
        {
            ["groups.ldk.details"] = "Gruppenhinweis.",
            ["commands.ldk-show.details"] = "Nach der Einrichtung ist ein Neustart notwendig.\n\n  /a   erster Eintrag\n  /b   zweiter Eintrag",
        });

        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, resolver).RunAsync(["ldk", "show", "--help"]));
        AssertEx.True(console.StandardOutput.Contains(
            "\nNach der Einrichtung ist ein Neustart notwendig.\n\n  /a   erster Eintrag\n  /b   zweiter Eintrag\n", StringComparison.Ordinal), console.StandardOutput);
        AssertEx.True(!console.StandardOutput.Contains("Restart afterwards.", StringComparison.Ordinal), console.StandardOutput);

        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console, resolver).RunAsync(["ldk", "--help"]));
        AssertEx.True(console.StandardOutput.Contains("\nGruppenhinweis.\n", StringComparison.Ordinal), console.StandardOutput);

        // Without a translation the literal is the fallback.
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["ldk", "show", "--help"]));
        AssertEx.True(console.StandardOutput.Contains("\nRestart afterwards.\n", StringComparison.Ordinal), console.StandardOutput);
        console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["ldk", "--help"]));
        AssertEx.True(console.StandardOutput.Contains("\nGroup notes.\n", StringComparison.Ordinal), console.StandardOutput);

        // A key alone, without a literal, gives extended help only when it resolves.
        var builder = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(builder, "keyed").WithHelp(new CommandHelp("Keyed.") { LongDescriptionKey = "keyed.details" });
        CommandCatalog catalog = builder.Build();
        string help = CommandHelpFormatter.Format(catalog, "tool", new CommandPath(["keyed"]), new CommandTextContext(CultureInfo.InvariantCulture, resolver));
        AssertEx.True(help.StartsWith("Usage: tool keyed [options]\n\nKeyed.\n\nOptions:", StringComparison.Ordinal), help);
        var keyed = new CommandTextContext(CultureInfo.InvariantCulture, new TextResolver(new Dictionary<string, string> { ["keyed.details"] = "Details." }));
        help = CommandHelpFormatter.Format(catalog, "tool", new CommandPath(["keyed"]), keyed);
        AssertEx.True(help.StartsWith("Usage: tool keyed [options]\n\nKeyed.\n\nDetails.\n\nOptions:", StringComparison.Ordinal), help);
        AssertEx.Equal("Details.", keyed.LongDescription(catalog.Commands.Single(static command => command.Name == "keyed").Help));
        AssertEx.Equal(null, CommandTextContext.English.LongDescription(CommandHelp.Empty));
    }

    [Test, DisplayName("help-texts/unresolved-long-description-keys-are-reported")]
    public ValueTask UnresolvedLongDescriptionKeys()
    {
        var context = new CommandTextContext(CultureInfo.GetCultureInfo("de"), new TextResolver(new Dictionary<string, string> { ["groups.ldk.details"] = "Gruppe." }));
        IReadOnlyList<CommandCatalogIssue> issues = context.ValidateDescriptionKeys(GeneratedCommandCatalog.Create());
        CommandCatalogIssue issue = issues.Single(static issue => issue.Location == "ldk show" && issue.Message.Contains("commands.ldk-show.details", StringComparison.Ordinal));
        AssertEx.Equal("RCLI0023", issue.Code);
        AssertEx.Equal("Long description key 'commands.ldk-show.details' for long description of command 'ldk show' did not resolve in culture 'de'; the literal extended help is shown instead.",
            issue.Message);
        AssertEx.True(!issues.Any(static issue => issue.Message.Contains("groups.ldk.details", StringComparison.Ordinal)), string.Join("\n", issues.Select(static issue => issue.Message)));

        // An empty key is a catalog error, like an empty DescriptionKey.
        var builder = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(builder, "blank").WithHelp(new CommandHelp("Blank.") { LongDescriptionKey = " " });
        CommandCatalogValidationException error = AssertEx.Throws<CommandCatalogValidationException>(() => builder.Build());
        AssertEx.True(error.Issues.Any(static issue => issue.Code == "RCLI0003" && issue.Message == "The long description key cannot be empty."),
            string.Join("\n", error.Issues.Select(static issue => issue.Message)));
        return ValueTask.CompletedTask;
    }

    [Test, DisplayName("help-texts/generator-emits-and-checks-long-description-keys")]
    public ValueTask GeneratorLongDescriptionKeys()
    {
        CSharpCompilation compilation = GeneratorTests.CreateCompilation("""
            using Runic.CommandLine;

            namespace Probe;

            [CommandGroup("tools", LongDescription = "Group notes.", LongDescriptionKey = "groups.tools.details")]
            [CommandGroup("blank", LongDescriptionKey = "")]
            internal static class Commands
            {
                [Command("tools show", LongDescription = "Notes.", LongDescriptionKey = "commands.tools-show.details")]
                public static string Show() => "";

                [Command("blank show")]
                public static string Blank() => "";

                [Command("broken", LongDescriptionKey = " ")]
                public static string Broken() => "";
            }
            """);
        GeneratorRunResult result = CSharpGeneratorDriver.Create(new CommandLineGenerator()).RunGenerators(compilation).GetRunResult().Results.Single();
        ImmutableArray<Diagnostic> diagnostics = result.Diagnostics;
        AssertEx.SequenceEqual(["Command 'blank' has invalid long description key: ''", "Command 'broken' has invalid long description key: ' '"],
            diagnostics.Where(static diagnostic => diagnostic.Id == "RCLI9005").Select(static diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal).ToArray(),
            string.Join("\n", diagnostics));
        string generated = string.Concat(result.GeneratedSources.Select(static item => item.SourceText.ToString()));
        AssertEx.True(generated.Contains("LongDescription = \"Group notes.\", LongDescriptionKey = \"groups.tools.details\"", StringComparison.Ordinal), generated);
        AssertEx.True(generated.Contains("LongDescription = \"Notes.\", LongDescriptionKey = \"commands.tools-show.details\"", StringComparison.Ordinal), generated);
        return ValueTask.CompletedTask;
    }

    // The commands of #98: arguments with value names, and options related by ID.
    private static CommandCatalog PolicyCatalog(CommandSyntaxDialect syntax)
    {
        var builder = new CommandCatalogBuilder().Syntax(syntax);
        CatalogTests.ValidCommand(builder, "link")
            .Argument("policy-name", "policy-name", CommandArity.ExactlyOne)
            .Argument("container-path", "container-path", CommandArity.ExactlyOne)
            .ParameterHelp("policy-name", new CommandHelp(valueName: "Richtlinie"))
            .ParameterHelp("container-path", new CommandHelp("Pfad des Containers.", valueName: "Container-Pfad"));
        CatalogTests.ValidCommand(builder, "alias")
            .Option("list", "-l", CommandArity.Zero)
            .Option("remove", "-r", CommandArity.ExactlyOne)
            .ParameterHelp("list", new CommandHelp("Listet die Aliasse auf"))
            .ParameterHelp("remove", new CommandHelp("Entfernt die Einträge für <AltName>", valueName: "AltName") { ConflictsWith = ["list"] });
        CatalogTests.ValidCommand(builder, "wait")
            .Option("factor", "--factor", CommandArity.ExactlyOne)
            .Option("limit", "--limit", CommandArity.ExactlyOne)
            .ParameterHelp("factor", new CommandHelp("Faktor", defaultValue: "1.5"))
            .ParameterHelp("limit", new CommandHelp("Obergrenze", defaultValue: "10000") { Requires = ["factor"] });
        return builder.Build();
    }

    private static async ValueTask<TestCommandConsole> Run(CommandCatalog catalog, int exitCode, params string[] args)
    {
        var console = new TestCommandConsole();
        var app = new CommandApp(catalog) { Name = "tool", Console = console, HandleTerminationSignals = false, ParseSettings = ParseSettings.Default };
        AssertEx.Equal(exitCode, await app.RunAsync(args), console.StandardError);
        return console;
    }

    [Test, DisplayName("help-texts/missing-argument-names-the-value-name")]
    public async ValueTask MissingArgumentNamesTheValueName()
    {
        foreach ((CommandSyntaxDialect syntax, string link, string help) in new[]
        {
            (CommandSyntaxDialect.Portable, "link", "--help"), (CommandSyntaxDialect.WindowsSlash, "/link", "/?"),
        })
        {
            TestCommandConsole console = await Run(PolicyCatalog(syntax), CommandExitCodes.Usage, link, "one");
            AssertEx.Equal(
                "The <Container-Pfad> argument is missing.\n" +
                "Usage: tool " + link + " <Richtlinie> <Container-Pfad> [options]\n" +
                "Run \"tool " + link + " " + help + "\" for more information.\n",
                console.StandardError);
        }

        // An argument without a value name is named as the usage line shows it, by its name.
        var builder = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(builder, "copy").Argument("source", "source", CommandArity.ExactlyOne);
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(builder.Build(), ["copy"], ParseSettings.Default);
        AssertEx.SequenceEqual(["<source>"], outcome.Diagnostics.Single().Arguments);
        AssertEx.Equal("The <source> argument is missing.", outcome.Diagnostics.Single().Message);
    }

    [Test, DisplayName("help-texts/help-names-arguments-and-related-options-as-users-see-them")]
    public async ValueTask HelpNamesWhatUsersSee()
    {
        TestCommandConsole console = await Run(PolicyCatalog(CommandSyntaxDialect.WindowsSlash), 0, "/link", "/?");
        AssertEx.True(console.StandardOutput.StartsWith(
            "Usage: tool /link <Richtlinie> <Container-Pfad> [options]\n\n" +
            "Arguments:\n" +
            "  <Richtlinie>      [required]\n" +
            "  <Container-Pfad>  Pfad des Containers. [required]\n", StringComparison.Ordinal), console.StandardOutput);

        console = await Run(PolicyCatalog(CommandSyntaxDialect.WindowsSlash), 0, "/alias", "/?");
        AssertEx.True(console.StandardOutput.Contains("Entfernt die Einträge für <AltName> [conflicts: /l]\n", StringComparison.Ordinal), console.StandardOutput);
        console = await Run(PolicyCatalog(CommandSyntaxDialect.WindowsSlash), 0, "/wait", "/?");
        AssertEx.True(console.StandardOutput.Contains("Obergrenze [default: 10000] [requires: /factor]\n", StringComparison.Ordinal), console.StandardOutput);

        console = await Run(PolicyCatalog(CommandSyntaxDialect.Portable), 0, "alias", "--help");
        AssertEx.True(console.StandardOutput.Contains("Entfernt die Einträge für <AltName> [conflicts: -l]\n", StringComparison.Ordinal), console.StandardOutput);
        console = await Run(PolicyCatalog(CommandSyntaxDialect.Portable), 0, "wait", "--help");
        AssertEx.True(console.StandardOutput.Contains("Obergrenze [default: 10000] [requires: --factor]\n", StringComparison.Ordinal), console.StandardOutput);

        // The usage error for the same conflict names the options the same way.
        console = await Run(PolicyCatalog(CommandSyntaxDialect.WindowsSlash), CommandExitCodes.Usage, "/alias", "/l", "/r", "x");
        AssertEx.True(console.StandardError.StartsWith("/r cannot be combined with /l.\n", StringComparison.Ordinal), console.StandardError);
    }

    [Test, DisplayName("help-texts/boolean-options-show-no-default")]
    public async ValueTask BooleanOptionsShowNoDefault()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["flags-default", "--help"]), console.StandardError);
        string help = console.StandardOutput;
        AssertEx.True(!help.Contains("False", StringComparison.Ordinal) && !help.Contains("false", StringComparison.Ordinal), help);
        AssertEx.True(help.Contains("Listet die Aliasse auf\n", StringComparison.Ordinal), help);
        AssertEx.True(help.Contains("Ohne Ausgabe\n", StringComparison.Ordinal), help);
        AssertEx.True(help.Contains("Faktor [default: 1.5]\n", StringComparison.Ordinal), help);

        // The generated catalog carries no display default for a Boolean option, declared '= false' or not.
        CommandDescriptor command = GeneratedCommandCatalog.Create().Commands.Single(static command => command.Name == "flags-default");
        AssertEx.Equal(null, command.Options.Single(static option => option.Id == "list").Help.DefaultValue);
        AssertEx.Equal(null, command.Options.Single(static option => option.Id == "quiet").Help.DefaultValue);
        AssertEx.Equal("1.5", command.Options.Single(static option => option.Id == "factor").Help.DefaultValue);

        // A hand-written flag with a display default shows none either; a value option keeps its default.
        var builder = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(builder, "manual")
            .Option("force", "--force", CommandArity.Zero)
            .Option("mode", "--mode", CommandArity.ExactlyOne)
            .ParameterHelp("force", new CommandHelp("Force.", defaultValue: "False"))
            .ParameterHelp("mode", new CommandHelp("Mode.", defaultValue: "fast"));
        help = CommandHelpFormatter.Format(builder.Build(), "tool", new CommandPath(["manual"]));
        AssertEx.True(help.Contains("Force.\n", StringComparison.Ordinal) && help.Contains("Mode. [default: fast]\n", StringComparison.Ordinal), help);
    }

    internal sealed class TextResolver(Dictionary<string, string> texts) : ICommandTextResolver
    {
        public string? Resolve(string key, CultureInfo culture, IReadOnlyList<string> arguments) =>
            texts.TryGetValue(key, out string? text) ? string.Format(CultureInfo.InvariantCulture, text, [.. arguments]) : null;
    }
}

// Hidden so the shared generated catalog's root help listings stay unchanged.
[CommandGroup("ldk", Hidden = true, LongDescription = "Group notes.", LongDescriptionKey = "groups.ldk.details")]
internal static class HelpTextCommands
{
    [Command("ldk show", Hidden = true, Description = "Show.", LongDescription = "Restart afterwards.", LongDescriptionKey = "commands.ldk-show.details")]
    internal static string Show() => "ok";

    [Command("flags-default", Hidden = true, Description = "Wartet.")]
    internal static string FlagsDefault([Option("--quiet", Description = "Ohne Ausgabe")] bool quiet,
        [Option("-l", Description = "Listet die Aliasse auf")] bool list = false,
        [Option("--factor", Description = "Faktor")] double factor = 1.5) => "ok";
}
