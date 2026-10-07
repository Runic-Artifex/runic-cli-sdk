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
        new("help/long-description-keeps-preformatted-lines", LongDescriptionKeepsPreformattedLines),
        new("help/row-description-lines-continue-under-the-column", RowDescriptionLines),
        new("help/wide-characters-use-two-cells-and-may-break", WideCharacters),
        new("help/notes-fit-narrow-widths", NotesFitNarrowWidths),
        new("help/row-descriptions-wrap-after-double-spaces", RowDescriptionsWrapAfterDoubleSpaces),
        new("help/unpaired-surrogates-do-not-fail", UnpairedSurrogates),
        new("help/explicit-spectre-width-is-used-as-given", ExplicitSpectreWidth),
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
        foreach (int width in Enumerable.Range(CommandHelpFormatter.MinimumWidth, 21).Append(CommandHelpFormatter.DefaultWidth).Append(120))
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
        // Spectre may still style headings when it detects a CI terminal; compare the visible text.
        string visible = System.Text.RegularExpressions.Regex.Replace(inner.StandardOutput, "\u001b\\[[0-9;]*m", "");
        AssertEx.Equal(expected, visible.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string Convert(string? longDescription, string optionDescription, int width)
    {
        var builder = new CommandCatalogBuilder();
        CatalogTests.ValidCommand(builder, "convert")
            .WithHelp(new CommandHelp("Convert records.") { LongDescription = longDescription })
            .Option("mode", "--mode", CommandArity.ExactlyOne)
            .ParameterHelp("mode", new CommandHelp(optionDescription, defaultValue: "fast"));
        return CommandHelpFormatter.Format(builder.Build(), "fixture", new CommandPath(["convert"]), CommandTextContext.English, "--output", width);
    }

    private static ValueTask LongDescriptionKeepsPreformattedLines()
    {
        string help = Convert("""
            Formats:
              - json   machine readable
              - csv    spreadsheets

                fixture convert --mode csv > out.csv

            - a list item that is long enough to wrap onto a second line
            1. a numbered item that is also long enough to wrap around
            Plain paragraph text that is long enough to wrap at forty columns.
            """, "Mode.", 40);
        const string expected = """
            Usage: fixture convert [options]

            Convert records.

            Formats:
              - json   machine readable
              - csv    spreadsheets

                fixture convert --mode csv > out.csv

            - a list item that is long enough to
              wrap onto a second line
            1. a numbered item that is also long
               enough to wrap around
            Plain paragraph text that is long enough
            to wrap at forty columns.

            Options:
              --mode <mode>  Mode. [default: fast]
              -h, --help     Show help
              --version      Show version
              --output <human|json>
                             Select output format

            """;
        AssertEx.Equal(expected, help);
        return ValueTask.CompletedTask;
    }

    private static ValueTask RowDescriptionLines()
    {
        string help = Convert(null, "First line of the description, long enough to wrap.\nSecond line\n  indented   example\n\nAfter a blank line", 50);
        const string expected = """
            Usage: fixture convert [options]

            Convert records.

            Options:
              --mode <mode>  First line of the description,
                             long enough to wrap.
                             Second line
                             indented example

                             After a blank line
                             [default: fast]
              -h, --help     Show help
              --version      Show version
              --output <human|json>
                             Select output format

            """;
        AssertEx.Equal(expected, help);
        return ValueTask.CompletedTask;
    }

    private static ValueTask WideCharacters()
    {
        string help = Convert("出力形式を選択します。既定の形式は機械可読なJSONです。", "モードを選びます。e\u0301te\u0301 ＡＢＣ", 30);
        // {ETE} is "été" spelled with combining U+0301 accents, which take no cell.
        string expected = """
            Usage: fixture convert
                   [options]

            Convert records.

            出力形式を選択します。既定の形
            式は機械可読なJSONです。

            Options:
              --mode <mode>
                          モードを選びま
                          す。{ETE} ＡＢＣ
                          [default: fast]
              -h, --help  Show help
              --version   Show version
              --output <human|json>
                          Select output
                          format

            """.Replace("{ETE}", "e\u0301te\u0301", StringComparison.Ordinal);
        AssertEx.Equal(expected, help);
        return ValueTask.CompletedTask;
    }

    private static ValueTask NotesFitNarrowWidths()
    {
        for (int width = CommandHelpFormatter.MinimumWidth; width < 40; width++)
        {
            // A term exactly as wide as the column cap and a note half the line wide: the note must still fit.
            int cap = Math.Min(32, Math.Max(8, width * 2 / 5));
            string value = new('v', Math.Max(1, width / 2 - "[default: ]".Length));
            var builder = new CommandCatalogBuilder();
            CatalogTests.ValidCommand(builder, "run")
                .Option("wide", "--" + new string('w', cap - 2), CommandArity.Zero)
                .ParameterHelp("wide", new CommandHelp(defaultValue: value));
            string help = CommandHelpFormatter.Format(builder.Build(), "fixture", new CommandPath(["run"]), CommandTextContext.English, "--output", width);
            foreach (string line in help.Split('\n'))
            {
                // Only one word wider than the space after the column may overflow; a kept note never does.
                int column = 4 + cap;
                bool fits = line.Length <= width || UnbreakableTerms.Contains(line.Trim()) ||
                    (line.Length > column && !line[column..].Contains(' ', StringComparison.Ordinal));
                AssertEx.True(fits, $"width {width}: '{line}'");
            }
            AssertEx.True(help.Contains("[default:", StringComparison.Ordinal) && help.Contains(value + "]", StringComparison.Ordinal), help);
        }
        return ValueTask.CompletedTask;
    }

    private static ValueTask RowDescriptionsWrapAfterDoubleSpaces()
    {
        string help = Convert(null, "Selects the mode.  The default mode is fast and suits most inputs.", 40);
        AssertEx.True(help.Contains("""
              --mode <mode>  Selects the mode. The
                             default mode is fast
            """.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal), help);
        return ValueTask.CompletedTask;
    }

    private static ValueTask UnpairedSurrogates()
    {
        foreach (string text in new[] { "Bad \uD800 text", "Bad\uDC00text", "末尾\uD800", "\uD800\u0301 x" })
        {
            string help = Convert(text, text, 40);
            AssertEx.True(help.Contains("Usage: fixture", StringComparison.Ordinal), help);
            AssertEx.True(help.Contains("[default: fast]", StringComparison.Ordinal), help);
        }
        return ValueTask.CompletedTask;
    }

    private static async ValueTask ExplicitSpectreWidth()
    {
        // A terminal-looking console with an explicit width: the presenter must not subtract the terminal margin.
        var inner = new TestCommandConsole { IsOutputRedirected = false };
        var console = new SpectreCommandConsole(inner, width: 60, color: false);
        await new SpectreHelpPresenter().WriteAsync(Catalog(), "fixture", new CommandPath(["export"]), "--output", console, CancellationToken.None);
        string expected = CommandHelpFormatter.Format(Catalog(), "fixture", new CommandPath(["export"]), CommandTextContext.English, "--output", 60);
        string visible = System.Text.RegularExpressions.Regex.Replace(inner.StandardOutput, "\u001b\\[[0-9;]*m", "");
        AssertEx.Equal(expected, visible.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
}
