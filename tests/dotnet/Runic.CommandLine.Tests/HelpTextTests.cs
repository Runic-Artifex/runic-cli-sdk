using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Generators;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

// Help and usage texts as users read them: extended help through a text key (#97).
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
}
