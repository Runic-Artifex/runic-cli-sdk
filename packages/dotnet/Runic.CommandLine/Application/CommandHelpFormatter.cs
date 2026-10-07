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
    /// The maximum line width. Descriptions wrap at word boundaries and continue under
    /// their description column; a single word longer than the available space is not split.
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
        AppendWrapped(text, usage.ToString(), usagePrefix.Length, usagePrefix.Length, width);
        if (command is not null && context.Description(command.Help, command.DescriptionKey) is { Length: > 0 } description)
        {
            text.Append('\n');
            AppendParagraphs(text, description, width);
        }
        if (command?.Help.LongDescription is { } details)
        {
            text.Append('\n');
            AppendParagraphs(text, details, width);
        }
        var rows = new List<(string Term, string Description)>();
        foreach (CommandDescriptor child in (command?.Subcommands ?? catalog.Commands).Where(child => !child.Help.Hidden))
            rows.Add((child.Name + (ReferenceEquals(child, catalog.DefaultCommand) ? " (" + context.Resolve("help.default-command", "default") + ")" : "") +
                (child.Aliases.Count > 0 ? " (" + string.Join(", ", child.Aliases) + ")" : ""), context.Description(child.Help, child.DescriptionKey)));
        if (path.Count == 0 && !catalog.TryGetCommand("completion", out _))
            rows.Add(("completion <shell>", context.Resolve("help.completion", "Generate bash, zsh, fish or PowerShell completions")));
        AppendSection(text, context.Resolve("help.commands", "Commands"), rows, width);
        rows.Clear();
        if (command is not null)
            foreach (CommandArgumentDescriptor argument in command.Arguments.Where(argument => !argument.Help.Hidden))
                rows.Add((argument.Name, Describe(argument.Help, argument.DescriptionKey, argument.IsSensitive, argument.Arity.Minimum > 0, context, width)));
        AppendSection(text, context.Resolve("help.arguments", "Arguments"), rows, width);
        rows.Clear();
        foreach (CommandOptionDescriptor option in (command?.Options ?? CommandCompletion.GlobalOptions(catalog)).Where(option => !option.Help.Hidden))
            rows.Add((string.Join(", ", new[] { option.Name }.ConcatAliases(option.Aliases)) +
                (option.Arity.Maximum != 0 ? " <" + (option.Help.ValueName ?? option.Id) + (option.Arity.Maximum != 1 ? "..." : "") + ">" : ""),
                Describe(option.Help, option.DescriptionKey, option.IsSensitive, option.IsRequired, context, width)));
        rows.Add(("-h, --help", context.Resolve("help.show-help", "Show help")));
        rows.Add(("--version", context.Resolve("help.show-version", "Show version")));
        rows.Add((outputOptionName + " <human|json>", context.Resolve("help.output-format", "Select output format")));
        AppendSection(text, context.Resolve("help.options", "Options"), rows, width);
        if (command?.Help.Examples.Count > 0)
        {
            // Examples are copied into a shell, so they are never wrapped.
            text.Append('\n').Append(context.Resolve("help.examples", "Examples")).Append(":\n");
            foreach (string example in command.Help.Examples) text.Append("  ").Append(example).Append('\n');
        }
        return text.ToString();
    }

    private static string Describe(CommandHelp help, string? fallback, bool sensitive, bool required, CommandTextContext context, int width)
    {
        var text = new StringBuilder(context.Description(help, fallback));
        void Note(string label, string? value = null)
        {
            if (text.Length > 0) text.Append(' ');
            string note = "[" + label + (value is null ? "" : ": " + value) + "]";
            // Keep a short bracketed note on one line; a long one may still wrap between words.
            text.Append(note.Length <= width / 2 ? note.Replace(' ', NoBreak) : note);
        }
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
        return text.ToString();
    }

    // A noncharacter that never occurs in presentation text joins the words of one note.
    private const char NoBreak = '\uFFFF';

    private static void AppendSection(StringBuilder text, string heading, List<(string Term, string Description)> rows, int width)
    {
        if (rows.Count == 0) return;
        text.Append('\n').Append(heading).Append(":\n");
        // Two-space indent, the widest term that fits the cap, then a two-space gap.
        int cap = Math.Min(MaximumTermColumn, Math.Max(8, width * 2 / 5));
        int termWidth = rows.Select(row => row.Term.Length).Where(length => length <= cap).DefaultIfEmpty(cap).Max();
        int column = 2 + termWidth + 2;
        foreach ((string term, string description) in rows)
        {
            text.Append("  ").Append(term);
            if (description.Length == 0)
            {
                text.Append('\n');
                continue;
            }
            if (term.Length > termWidth)
            {
                text.Append('\n').Append(' ', column);
            }
            else
            {
                text.Append(' ', column - 2 - term.Length);
            }
            AppendWrapped(text, description, column, column, width);
        }
    }

    private static void AppendParagraphs(StringBuilder text, string value, int width)
    {
        foreach (string line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) text.Append('\n');
            else AppendWrapped(text, line, 0, 0, width);
        }
    }

    // Appends words separated by single spaces, starting at column 'start' on the current line,
    // breaking before a word that would pass 'width' and indenting continuation lines to 'indent'.
    private static void AppendWrapped(StringBuilder text, string value, int start, int indent, int width)
    {
        int position = start;
        bool lineHasWord = false;
        foreach (string word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (lineHasWord && position + 1 + word.Length > width)
            {
                text.Append('\n').Append(' ', indent);
                position = indent;
                lineHasWord = false;
            }
            if (lineHasWord)
            {
                text.Append(' ');
                position++;
            }
            text.Append(word.Replace(NoBreak, ' '));
            position += word.Length;
            lineHasWord = true;
        }
        text.Append('\n');
    }

    private static IEnumerable<string> ConcatAliases(this string[] names, IReadOnlyList<string> aliases)
    {
        foreach (string name in names) yield return name;
        foreach (string alias in aliases) yield return alias;
    }
}
