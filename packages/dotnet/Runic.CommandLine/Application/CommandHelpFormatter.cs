using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Runic.CommandLine;

/// <summary>Formats discoverable help directly from the command catalog.</summary>
public static class CommandHelpFormatter
{
    /// <summary>The line width used when the caller does not supply a terminal width.</summary>
    public const int DefaultWidth = 80;

    /// <summary>The narrowest supported line width.</summary>
    public const int MinimumWidth = 20;

    // A term wider than this moves its description to the next line instead of widening the whole column.
    private const int MaximumTermColumn = 32;

    /// <summary>Formats help for a root or resolved command path.</summary>
    public static string Format(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName = "--output")
    {
        return Format(catalog, applicationName, path, new CommandTextContext(CultureInfo.GetCultureInfo("en")), outputOptionName);
    }

    /// <summary>Formats help using invocation-local culture and text resolution.</summary>
    /// <param name="catalog">The command catalog.</param>
    /// <param name="applicationName">The name shown in the usage line.</param>
    /// <param name="path">The root or a resolved command path.</param>
    /// <param name="context">Culture and text resolution for presentation text.</param>
    /// <param name="outputOptionName">The spelling of the output-mode option.</param>
    /// <param name="width">
    /// The maximum line width in terminal cells. Descriptions wrap at word boundaries (and between wide East Asian
    /// characters) and continue under their description column; a single word longer than the available space is
    /// not split. LongDescription lines that are indented or contain a run of three or more spaces are kept as written.
    /// </param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "The required CommandTextContext parameter keeps calls unambiguous; the earlier overload is unchanged.")]
    public static string Format(CommandCatalog catalog, string applicationName, CommandPath path, CommandTextContext context, string outputOptionName = "--output", int width = DefaultWidth)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, MinimumWidth);
        CommandDescriptor? command = null;
        foreach (string segment in path.Segments)
        {
            if (command is null) catalog.TryGetCommand(segment, out command);
            else command.TryGetSubcommand(segment, out command);
        }
        var text = new StringBuilder();
        string usagePrefix = context.Resolve("help.usage", "Usage") + ": ";
        var usage = new StringBuilder(applicationName);
        if (path.Count > 0) usage.Append(' ').Append(path);
        else if (catalog.Commands.Count > 0) usage.Append(" <").Append(context.Resolve("help.command-placeholder", "command")).Append('>');
        if (command is not null)
            foreach (CommandArgumentDescriptor argument in command.Arguments.Where(argument => !argument.Help.Hidden))
                usage.Append(' ').Append(argument.Arity.Minimum == 0 ? '[' : '<').Append(argument.Help.ValueName ?? argument.Name)
                    .Append(argument.Arity.Maximum != 1 ? "..." : "").Append(argument.Arity.Minimum == 0 ? ']' : '>');
        usage.Append(" [").Append(context.Resolve("help.options-placeholder", "options")).Append(']');
        text.Append(usagePrefix);
        int usageIndent = CellWidth(usagePrefix);
        AppendWrapped(text, usage.ToString(), usageIndent, usageIndent, width);
        if (command is not null && context.Description(command.Help, command.DescriptionKey) is { Length: > 0 } description)
        {
            text.Append('\n');
            AppendParagraphs(text, description, width, preformatted: false);
        }
        if (command?.Help.LongDescription is { } details)
        {
            text.Append('\n');
            AppendParagraphs(text, details, width, preformatted: true);
        }
        var rows = new List<Row>();
        foreach (CommandDescriptor child in (command?.Subcommands ?? catalog.Commands).Where(child => !child.Help.Hidden))
            rows.Add(new(child.Name + (ReferenceEquals(child, catalog.DefaultCommand) ? " (" + context.Resolve("help.default-command", "default") + ")" : "") +
                (child.Aliases.Count > 0 ? " (" + string.Join(", ", child.Aliases) + ")" : ""), context.Description(child.Help, child.DescriptionKey)));
        if (path.Count == 0 && !catalog.TryGetCommand("completion", out _))
            rows.Add(new("completion <shell>", context.Resolve("help.completion", "Generate bash, zsh, fish or PowerShell completions")));
        AppendSection(text, context.Resolve("help.commands", "Commands"), rows, width);
        rows.Clear();
        if (command is not null)
            foreach (CommandArgumentDescriptor argument in command.Arguments.Where(argument => !argument.Help.Hidden))
                rows.Add(new(argument.Name, context.Description(argument.Help, argument.DescriptionKey), Notes(argument.Help, argument.IsSensitive, argument.Arity.Minimum > 0, context)));
        AppendSection(text, context.Resolve("help.arguments", "Arguments"), rows, width);
        rows.Clear();
        foreach (CommandOptionDescriptor option in (command?.Options ?? CommandCompletion.GlobalOptions(catalog)).Where(option => !option.Help.Hidden))
            rows.Add(new(string.Join(", ", new[] { option.Name }.ConcatAliases(option.Aliases)) +
                (option.Arity.Maximum != 0 ? " <" + (option.Help.ValueName ?? option.Id) + (option.Arity.Maximum != 1 ? "..." : "") + ">" : ""),
                context.Description(option.Help, option.DescriptionKey), Notes(option.Help, option.IsSensitive, option.IsRequired, context)));
        rows.Add(new("-h, --help", context.Resolve("help.show-help", "Show help")));
        rows.Add(new("--version", context.Resolve("help.show-version", "Show version")));
        rows.Add(new(outputOptionName + " <human|json>", context.Resolve("help.output-format", "Select output format")));
        AppendSection(text, context.Resolve("help.options", "Options"), rows, width);
        if (command?.Help.Examples.Count > 0)
        {
            // Examples are copied into a shell, so they are never wrapped.
            text.Append('\n').Append(context.Resolve("help.examples", "Examples")).Append(":\n");
            foreach (string example in command.Help.Examples) text.Append("  ").Append(example).Append('\n');
        }
        return text.ToString();
    }

    private sealed record Row(string Term, string Description, IReadOnlyList<string>? Notes = null);

    private static List<string> Notes(CommandHelp help, bool sensitive, bool required, CommandTextContext context)
    {
        var notes = new List<string>();
        void Note(string label, string? value = null) => notes.Add("[" + label + (value is null ? "" : ": " + value) + "]");
        if (required) Note(context.Resolve("help.required", "required"));
        if (!sensitive && !string.IsNullOrEmpty(help.DefaultValue)) Note(context.Resolve("help.default", "default"), help.DefaultValue);
        if (!sensitive && help.Choices.Count > 0) Note(context.Resolve("help.choices", "choices"), string.Join(", ", help.Choices));
        if (help.EnvironmentVariable is { } environment) Note(context.Resolve("help.environment", "env"), environment);
        if (help.PathKind != CommandPathKind.None)
        {
            string kind = help.PathKind.ToString().ToLowerInvariant();
            Note((help.MustExist ? context.Resolve("help.existing", "existing") + " " : "") + context.Resolve("help.path-kind." + kind, kind));
        }
        if (help.Minimum is { } min) Note(context.Resolve("help.minimum", "min"), min.ToString(CultureInfo.InvariantCulture));
        if (help.Maximum is { } max) Note(context.Resolve("help.maximum", "max"), max.ToString(CultureInfo.InvariantCulture));
        if (help.Requires.Count > 0) Note(context.Resolve("help.requires", "requires"), string.Join(", ", help.Requires));
        if (help.ConflictsWith.Count > 0) Note(context.Resolve("help.conflicts", "conflicts"), string.Join(", ", help.ConflictsWith));
        return notes;
    }

    // A noncharacter that never occurs in presentation text joins the words of one note.
    private const char NoBreak = '\uFFFF';

    private static void AppendSection(StringBuilder text, string heading, List<Row> rows, int width)
    {
        if (rows.Count == 0) return;
        text.Append('\n').Append(heading).Append(":\n");
        // Two-space indent, the widest term that fits the cap, then a two-space gap.
        int cap = Math.Min(MaximumTermColumn, Math.Max(8, width * 2 / 5));
        int termWidth = rows.Select(row => CellWidth(row.Term)).Where(length => length <= cap).DefaultIfEmpty(cap).Max();
        int column = 2 + termWidth + 2;
        // A short note stays on one line, but never wider than half the line or the space after the column.
        int noteLimit = Math.Min(width / 2, width - column);
        foreach (Row row in rows)
        {
            var body = new StringBuilder(row.Description.TrimEnd());
            foreach (string note in row.Notes ?? [])
            {
                if (body.Length > 0) body.Append(' ');
                body.Append(CellWidth(note) <= noteLimit ? note.Replace(' ', NoBreak) : note);
            }
            int termCells = CellWidth(row.Term);
            text.Append("  ").Append(row.Term);
            if (body.Length == 0)
            {
                text.Append('\n');
                continue;
            }
            if (termCells > termWidth) text.Append('\n').Append(' ', column);
            else text.Append(' ', column - 2 - termCells);
            AppendBlock(text, body.ToString(), column, width, preformatted: false);
        }
    }

    private static void AppendParagraphs(StringBuilder text, string value, int width, bool preformatted) => AppendBlock(text, value, 0, width, preformatted);

    // Appends text whose first line starts at the current position, which is column 'indent'. Each source line
    // continues at 'indent' and wraps, and a list item's continuation lines hang under its text. When 'preformatted'
    // is set (LongDescription), a line that is indented or contains a run of three or more spaces is a list, aligned
    // columns or a code block and is kept verbatim.
    private static void AppendBlock(StringBuilder text, string value, int indent, int width, bool preformatted)
    {
        string[] lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd();
            if (line.Length == 0)
            {
                TrimTrailingSpaces(text);
                text.Append('\n');
                continue;
            }
            if (index > 0) text.Append(' ', indent);
            if (preformatted && (char.IsWhiteSpace(line[0]) || line.Contains("   ", StringComparison.Ordinal)))
            {
                text.Append(line.Replace(NoBreak, ' ')).Append('\n');
                continue;
            }
            AppendWrapped(text, line, indent, indent + ListMarkerWidth(line), width);
        }
    }

    // "- ", "* " and "1. " start a list item; continuation lines align with the item's text.
    private static int ListMarkerWidth(string line)
    {
        if (line.Length > 2 && line[0] is '-' or '*' && line[1] == ' ') return 2;
        int digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
        return digits is > 0 and < 4 && line.Length > digits + 2 && line[digits] is '.' or ')' && line[digits + 1] == ' ' ? digits + 2 : 0;
    }

    private static void TrimTrailingSpaces(StringBuilder text)
    {
        while (text.Length > 0 && text[^1] == ' ') text.Length--;
    }

    // Appends words separated by single spaces, starting at column 'start' on the current line,
    // breaking before a word that would pass 'width' and indenting continuation lines to 'indent'.
    // Words never split, except between wide (CJK) characters, which may break anywhere.
    private static void AppendWrapped(StringBuilder text, string value, int start, int indent, int width)
    {
        int position = start;
        bool lineHasUnit = false;
        foreach ((string unit, bool spaceBefore) in Units(value))
        {
            int cells = CellWidth(unit);
            int gap = spaceBefore ? 1 : 0;
            if (lineHasUnit && position + gap + cells > width)
            {
                text.Append('\n').Append(' ', indent);
                position = indent;
                lineHasUnit = false;
            }
            if (lineHasUnit && spaceBefore)
            {
                text.Append(' ');
                position++;
            }
            text.Append(unit.Replace(NoBreak, ' '));
            position += cells;
            lineHasUnit = true;
        }
        text.Append('\n');
    }

    // Splits text into breakable units: space-separated words, and each wide character within a word.
    // Closing punctuation such as "。" stays with the character before it, so no line starts with it.
    private static List<(string Unit, bool SpaceBefore)> Units(string value)
    {
        var units = new List<(string Unit, bool SpaceBefore)>();
        void Add(string unit, bool spaceBefore)
        {
            if (!spaceBefore && units.Count > 0 && ClosingPunctuation.Contains(unit[0]))
                units[^1] = (units[^1].Unit + unit, units[^1].SpaceBefore);
            else units.Add((unit, spaceBefore));
        }
        bool first = true;
        foreach (string word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool spaceBefore = !first;
            first = false;
            int runStart = 0;
            int index = 0;
            while (index < word.Length)
            {
                Rune rune = RuneAt(word, index, out int length);
                if (IsWide(rune.Value))
                {
                    if (index > runStart)
                    {
                        Add(word[runStart..index], spaceBefore);
                        spaceBefore = false;
                    }
                    int end = index + length;
                    // Keep combining marks with the wide character they modify.
                    while (end < word.Length && CellWidth(RuneAt(word, end, out int next)) == 0) end += next;
                    Add(word[index..end], spaceBefore);
                    spaceBefore = false;
                    runStart = index = end;
                    continue;
                }
                index += length;
            }
            if (runStart < word.Length) Add(word[runStart..], spaceBefore);
        }
        return units;
    }

    private const string ClosingPunctuation = "、。，．）」』】〕〉》！？：；";

    // Terminal cells: wide and fullwidth characters take two, combining marks and other format characters none.
    private static int CellWidth(string value)
    {
        int cells = 0;
        foreach (Rune rune in value.EnumerateRunes()) cells += CellWidth(rune);
        return cells;
    }

    // Decodes one scalar; an unpaired surrogate becomes U+FFFD and advances by one UTF-16 unit.
    private static Rune RuneAt(string value, int index, out int length)
    {
        if (Rune.DecodeFromUtf16(value.AsSpan(index), out Rune rune, out length) == System.Buffers.OperationStatus.Done) return rune;
        length = 1;
        return Rune.ReplacementChar;
    }

    private static int CellWidth(Rune rune)
    {
        // Tab, the no-break sentinel and the soft hyphen (which terminals draw) take one cell.
        if (rune.Value is '\t' or NoBreak or 0x00AD) return 1;
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or UnicodeCategory.Control) return 0;
        return IsWide(rune.Value) ? 2 : 1;
    }

    // East Asian Wide and Fullwidth ranges (Unicode UAX #11): CJK, Hangul, kana, fullwidth forms and emoji with
    // default emoji presentation. Pairs of inclusive bounds in ascending order; an approximation for terminals,
    // which may differ for some symbols.
    private static ReadOnlySpan<int> WideRanges =>
    [
        0x1100, 0x115F, 0x231A, 0x231B, 0x2329, 0x232A, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3, 0x25FD, 0x25FE,
        0x2614, 0x2615, 0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1, 0x26AA, 0x26AB, 0x26BD, 0x26BE,
        0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA, 0x26F2, 0x26F3, 0x26F5, 0x26F5, 0x26FA, 0x26FA,
        0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B, 0x2728, 0x2728, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755,
        0x2757, 0x2757, 0x2795, 0x2797, 0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55,
        0x2E80, 0x303E, 0x3041, 0x33FF, 0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xA000, 0xA4CF, 0xA960, 0xA97F, 0xAC00, 0xD7A3,
        0xF900, 0xFAFF, 0xFE10, 0xFE19, 0xFE30, 0xFE6F, 0xFF00, 0xFF60, 0xFFE0, 0xFFE6,
        0x16FE0, 0x1B2FF, 0x1F004, 0x1F004, 0x1F0CF, 0x1F0CF, 0x1F18E, 0x1F18E, 0x1F191, 0x1F19A, 0x1F200, 0x1F2FF,
        0x1F300, 0x1F64F, 0x1F680, 0x1F6FF, 0x1F7E0, 0x1F7EB, 0x1F900, 0x1F9FF, 0x1FA70, 0x1FAFF,
        0x20000, 0x2FFFD, 0x30000, 0x3FFFD,
    ];

    private static bool IsWide(int value)
    {
        ReadOnlySpan<int> ranges = WideRanges;
        for (int index = 0; index < ranges.Length && value >= ranges[index]; index += 2)
            if (value <= ranges[index + 1]) return true;
        return false;
    }

    private static IEnumerable<string> ConcatAliases(this string[] names, IReadOnlyList<string> aliases)
    {
        foreach (string name in names) yield return name;
        foreach (string alias in aliases) yield return alias;
    }
}
