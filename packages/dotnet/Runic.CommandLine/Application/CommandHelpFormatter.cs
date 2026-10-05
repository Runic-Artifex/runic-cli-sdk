using System;
using System.Text;
using System.Linq;

namespace Runic.CommandLine;

/// <summary>Formats discoverable help directly from the command catalog.</summary>
public static class CommandHelpFormatter
{
    /// <summary>Formats help for a root or resolved command path.</summary>
    public static string Format(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName = "--output")
    {
        return Format(catalog, applicationName, path, new CommandTextContext(System.Globalization.CultureInfo.GetCultureInfo("en")), outputOptionName);
    }

    /// <summary>Formats help using invocation-local culture and text resolution.</summary>
    public static string Format(CommandCatalog catalog, string applicationName, CommandPath path, CommandTextContext context, string outputOptionName = "--output")
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(path);
        CommandDescriptor? command = null;
        foreach (string segment in path.Segments)
        {
            if (command is null) catalog.TryGetCommand(segment, out command);
            else command.TryGetSubcommand(segment, out command);
        }
        var text = new StringBuilder(context.Resolve("help.usage", "Usage")).Append(": ").Append(applicationName);
        if (path.Count > 0) text.Append(' ').Append(path);
        else if (catalog.Commands.Count > 0) text.Append(" <").Append(context.Resolve("help.command-placeholder", "command")).Append('>');
        if (command is not null)
            foreach (CommandArgumentDescriptor argument in command.Arguments.Where(argument => !argument.Help.Hidden))
                text.Append(' ').Append(argument.Arity.Minimum == 0 ? '[' : '<').Append(argument.Help.ValueName ?? argument.Name)
                    .Append(argument.Arity.Maximum != 1 ? "..." : "").Append(argument.Arity.Minimum == 0 ? ']' : '>');
        text.Append(" [").Append(context.Resolve("help.options-placeholder", "options")).Append("]\n");
        if (command is not null && context.Description(command.Help, command.DescriptionKey) is { Length: > 0 } description) text.Append('\n').Append(description).Append('\n');
        if (command?.Help.LongDescription is { } details) text.Append('\n').Append(details).Append('\n');
        var children = command?.Subcommands ?? catalog.Commands;
        if (children.Count > 0)
        {
            text.Append('\n').Append(context.Resolve("help.commands", "Commands")).Append(":\n");
            foreach (CommandDescriptor child in children.Where(child => !child.Help.Hidden))
                text.Append("  ").Append(child.Name).Append(ReferenceEquals(child, catalog.DefaultCommand) ? " (" + context.Resolve("help.default-command", "default") + ")" : "").Append(child.Aliases.Count > 0 ? " (" + string.Join(", ", child.Aliases) + ")" : "")
                    .Append("  ").Append(context.Description(child.Help, child.DescriptionKey)).Append('\n');
        }
        if (path.Count == 0 && !catalog.TryGetCommand("completion", out _)) text.Append("  completion <shell>  ").Append(context.Resolve("help.completion", "Generate bash, zsh, fish or PowerShell completions")).Append('\n');
        if (command?.Arguments.Count > 0)
        {
            text.Append('\n').Append(context.Resolve("help.arguments", "Arguments")).Append(":\n");
            foreach (CommandArgumentDescriptor argument in command.Arguments.Where(argument => !argument.Help.Hidden))
                Parameter(text, argument.Name, argument.Help, argument.DescriptionKey, argument.IsSensitive, argument.Arity.Minimum > 0, context);
        }
        text.Append('\n').Append(context.Resolve("help.options", "Options")).Append(":\n");
        foreach (CommandOptionDescriptor option in (command?.Options ?? CommandCompletion.GlobalOptions(catalog)).Where(option => !option.Help.Hidden))
            Parameter(text, string.Join(", ", new[] { option.Name }.ConcatAliases(option.Aliases)) +
                (option.Arity.Maximum != 0 ? " <" + (option.Help.ValueName ?? option.Id) + (option.Arity.Maximum != 1 ? "..." : "") + ">" : ""),
                option.Help, option.DescriptionKey, option.IsSensitive, option.IsRequired, context);
        text.Append("  -h, --help  ").Append(context.Resolve("help.show-help", "Show help"))
            .Append("\n  --version  ").Append(context.Resolve("help.show-version", "Show version"))
            .Append("\n  ").Append(outputOptionName).Append(" <human|json>  ").Append(context.Resolve("help.output-format", "Select output format")).Append('\n');
        if (command?.Help.Examples.Count > 0)
        {
            text.Append('\n').Append(context.Resolve("help.examples", "Examples")).Append(":\n");
            foreach (string example in command.Help.Examples) text.Append("  ").Append(example).Append('\n');
        }
        return text.ToString();
    }

    private static void Parameter(StringBuilder text, string name, CommandHelp help, string? fallback, bool sensitive, bool required, CommandTextContext context)
    {
        text.Append("  ").Append(name).Append("  ").Append(context.Description(help, fallback));
        if (required) text.Append(" [").Append(context.Resolve("help.required", "required")).Append(']');
        if (!sensitive && help.DefaultValue is { } value) text.Append(" [").Append(context.Resolve("help.default", "default")).Append(": ").Append(value).Append(']');
        if (!sensitive && help.Choices.Count > 0) text.Append(" [").Append(context.Resolve("help.choices", "choices")).Append(": ").AppendJoin(", ", help.Choices).Append(']');
        if (help.EnvironmentVariable is { } environment) text.Append(" [").Append(context.Resolve("help.environment", "env")).Append(": ").Append(environment).Append(']');
        if (help.PathKind != CommandPathKind.None) {
            text.Append(" [");
            if (help.MustExist) text.Append(context.Resolve("help.existing", "existing")).Append(' ');
            string kind = help.PathKind.ToString().ToLowerInvariant();
            text.Append(context.Resolve("help.path-kind." + kind, kind)).Append(']');
        }
        if (help.Minimum is { } min) text.Append(" [").Append(context.Resolve("help.minimum", "min")).Append(": ").Append(min.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
        if (help.Maximum is { } max) text.Append(" [").Append(context.Resolve("help.maximum", "max")).Append(": ").Append(max.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
        if (help.Requires.Count > 0) text.Append(" [").Append(context.Resolve("help.requires", "requires")).Append(": ").AppendJoin(", ", help.Requires).Append(']');
        if (help.ConflictsWith.Count > 0) text.Append(" [").Append(context.Resolve("help.conflicts", "conflicts")).Append(": ").AppendJoin(", ", help.ConflictsWith).Append(']');
        text.Append('\n');
    }

    private static System.Collections.Generic.IEnumerable<string> ConcatAliases(this string[] names, System.Collections.Generic.IReadOnlyList<string> aliases)
    {
        foreach (string name in names) yield return name;
        foreach (string alias in aliases) yield return alias;
    }
}
