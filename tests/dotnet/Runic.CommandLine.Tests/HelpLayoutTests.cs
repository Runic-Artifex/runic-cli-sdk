using Runic.CommandLine.Spectre;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal static class HelpLayoutTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("help/empty-default-is-omitted", EmptyDefaultIsOmitted),
        new("help/long-lines-wrap-under-the-description-column", LongLinesWrap),
        new("help/every-line-fits-the-requested-width", EveryLineFits),
        new("help/spectre-presenter-wraps-to-its-console-width", SpectreWrapsToConsoleWidth),
    ];

    private static readonly HashSet<string> UnbreakableTerms =
        ["--format, -f <format>", "--destination-directory-for-archives <DIR>", "--output <human|json>", "completion <shell>"];

    private static CommandCatalog Catalog()
    {
        var builder = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(builder, "export")
            .WithHelp(new CommandHelp("Export the selected records to a file in one of the supported formats, replacing any previous export."))
            .Option("name", "--name", CommandArity.ExactlyOne)
            .Option("format", "--format", CommandArity.ExactlyOne, aliases: ["-f"])
            .Option("destination", "--destination-directory-for-archives", CommandArity.ExactlyOne)
            .Option("quiet", "--quiet", CommandArity.Zero)
            .Argument("source", "source", CommandArity.ExactlyOne)
            .ParameterHelp("name", new CommandHelp("Archive name.", defaultValue: ""))
            .ParameterHelp("format", new CommandHelp("Output format used for every exported record, including attachments and metadata.", defaultValue: "json", choices: ["json", "csv", "yaml"]))
            .ParameterHelp("destination", new CommandHelp("Directory that receives the archive.", valueName: "DIR") { PathKind = CommandPathKind.Directory })
            .ParameterHelp("source", new CommandHelp("Record source."));
        return builder.Build();
    }

    private static ValueTask EmptyDefaultIsOmitted()
    {
        string help = CommandHelpFormatter.Format(Catalog(), "fixture", new CommandPath(["export"]));
        AssertEx.True(!help.Contains("[default: ]", StringComparison.Ordinal), help);
        AssertEx.True(help.Contains("Archive name.\n", StringComparison.Ordinal), help);
        AssertEx.True(help.Contains("[default: json]", StringComparison.Ordinal), help);
        return ValueTask.CompletedTask;
    }

    private static ValueTask LongLinesWrap()
    {
        string help = CommandHelpFormatter.Format(Catalog(), "fixture", new CommandPath(["export"]), CommandTextContext.English, "--output", 60);
        const string expected = """
            Usage: fixture export <source> [options]

            Export the selected records to a file in one of the
            supported formats, replacing any previous export.

            Arguments:
              source  Record source. [required]

            Options:
              --name <name>          Archive name.
              --format, -f <format>  Output format used for every
                                     exported record, including
                                     attachments and metadata.
                                     [default: json]
                                     [choices: json, csv, yaml]
              --destination-directory-for-archives <DIR>
                                     Directory that receives the
                                     archive. [directory]
              --quiet
              -h, --help             Show help
              --version              Show version
              --output <human|json>  Select output format

            """;
        AssertEx.Equal(expected.Replace("\r\n", "\n", StringComparison.Ordinal), help);
        return ValueTask.CompletedTask;
    }

    private static ValueTask EveryLineFits()
    {
        CommandCatalog catalog = Catalog();
        foreach (int width in new[] { CommandHelpFormatter.MinimumWidth, 40, CommandHelpFormatter.DefaultWidth, 120 })
            foreach (CommandPath path in new[] { CommandPath.Root, new CommandPath(["export"]) })
            {
                string help = CommandHelpFormatter.Format(catalog, "fixture", path, CommandTextContext.English, "--output", width);
                foreach (string line in help.Split('\n'))
                {
                    // Only a single word or an option term wider than the line may overflow; neither is split.
                    bool fits = line.Length <= width || !line.TrimStart().Contains(' ', StringComparison.Ordinal)
                        || UnbreakableTerms.Contains(line.Trim());
                    AssertEx.True(fits, $"width {width}: '{line}'");
                    AssertEx.True(line.Length == 0 || line[^1] != ' ', $"trailing space at width {width}: '{line}'");
                }
            }
        AssertEx.Throws<ArgumentOutOfRangeException>(() => CommandHelpFormatter.Format(catalog, "fixture", CommandPath.Root, CommandTextContext.English, "--output", CommandHelpFormatter.MinimumWidth - 1));
        return ValueTask.CompletedTask;
    }

    private static async ValueTask SpectreWrapsToConsoleWidth()
    {
        var inner = new TestCommandConsole();
        var console = new SpectreCommandConsole(inner, width: 60, color: false);
        await new SpectreHelpPresenter().WriteAsync(Catalog(), "fixture", new CommandPath(["export"]), "--output", console, CancellationToken.None);
        string expected = CommandHelpFormatter.Format(Catalog(), "fixture", new CommandPath(["export"]), CommandTextContext.English, "--output", 60);
        AssertEx.Equal(expected, inner.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
