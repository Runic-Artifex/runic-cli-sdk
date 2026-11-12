using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine;

/// <summary>Describes one immutable parser-neutral command node.</summary>
public sealed class CommandDescriptor
{
    private readonly Dictionary<string, CommandDescriptor> _subcommandsByName;
    private readonly Dictionary<string, CommandOptionDescriptor> _optionsByName;

    internal CommandDescriptor(
        string name,
        IReadOnlyList<string> aliases,
        string? descriptionKey,
        IReadOnlyList<CommandOptionDescriptor> options,
        IReadOnlyList<CommandArgumentDescriptor> arguments,
        IReadOnlyList<CommandDescriptor> subcommands,
        CommandRegistration registration,
        CommandHelp? help = null,
        string? spelling = null,
        StringComparer? comparer = null,
        CommandSyntaxDialect syntax = CommandSyntaxDialect.Portable,
        int declarationRank = 0)
    {
        Name = name;
        Spelling = spelling ?? name;
        Syntax = syntax;
        DeclarationRank = declarationRank;
        comparer ??= StringComparer.Ordinal;
        Aliases = aliases;
        DescriptionKey = descriptionKey;
        Help = help ?? CommandHelp.Empty;
        Options = options;
        Arguments = arguments;
        Subcommands = subcommands;
        Registration = registration;

        _subcommandsByName = new Dictionary<string, CommandDescriptor>(comparer);
        foreach (CommandDescriptor command in subcommands)
        {
            _subcommandsByName.Add(command.Name, command);
            foreach (string alias in command.Aliases)
            {
                _subcommandsByName.Add(alias, command);
            }

            // A display spelling matches as well; under case-insensitive matching it may differ from the name only by case.
            _subcommandsByName.TryAdd(command.Spelling, command);
        }

        _optionsByName = new Dictionary<string, CommandOptionDescriptor>(comparer);
        foreach (CommandOptionDescriptor option in options)
        {
            _optionsByName.Add(option.Name, option);
            foreach (string alias in option.Aliases)
            {
                _optionsByName.Add(alias, option);
            }
        }

        // Under the slash dialect every spelling also matches as /name: --count, -c and /count as /count and /c.
        if (syntax == CommandSyntaxDialect.WindowsSlash)
        {
            _optionsBySlashName = new Dictionary<string, CommandOptionDescriptor>(comparer);
            foreach (CommandOptionDescriptor option in options)
            {
                _optionsBySlashName.TryAdd(CommandDialect.Option(syntax, option.Name), option);
                foreach (string alias in option.Aliases) _optionsBySlashName.TryAdd(CommandDialect.Option(syntax, alias), option);
            }
        }
    }

    private readonly Dictionary<string, CommandOptionDescriptor>? _optionsBySlashName;

    /// <summary>
    /// Gets the spelling that help shows for the command and that the command line matches besides its name and
    /// aliases, such as <c>resetAll</c> for <c>reset-all</c>; the canonical <see cref="Name"/> unless the command
    /// declares a display spelling (<see cref="CommandAttribute.Spelling"/>). The name stays the command's identity
    /// in <see cref="ParsedInvocation.Path"/>, JSON output and diagnostics.
    /// </summary>
    public string Spelling { get; }

    // The catalog's dialect, so diagnostics outside the parser spell framework requests as the user does.
    internal CommandSyntaxDialect Syntax { get; }

    // The position among its siblings when help lists commands in declaration order.
    internal int DeclarationRank { get; }

    /// <summary>Gets shared help and value metadata.</summary>
    public CommandHelp Help { get; }

    /// <summary>Gets the canonical command name.</summary>
    public string Name { get; }

    /// <summary>Gets command aliases in registration order.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Gets the optional localization key for command help text.</summary>
    public string? DescriptionKey { get; }

    /// <summary>Gets command-local options in registration order.</summary>
    public IReadOnlyList<CommandOptionDescriptor> Options { get; }

    /// <summary>Gets positional arguments in binding order.</summary>
    public IReadOnlyList<CommandArgumentDescriptor> Arguments { get; }

    /// <summary>Gets child commands in registration order.</summary>
    public IReadOnlyList<CommandDescriptor> Subcommands { get; }

    /// <summary>Gets whether this node only groups child commands.</summary>
    public bool IsGroup => Registration is CommandGroupRegistration;

    internal CommandRegistration Registration { get; }

    /// <summary>
    /// Resolves a child command by canonical name, alias or display spelling, case-insensitively when the catalog
    /// declares <see cref="CommandCatalogBuilder.CaseInsensitive"/>.
    /// </summary>
    public bool TryGetSubcommand(string token, out CommandDescriptor? command)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _subcommandsByName.TryGetValue(token, out command);
    }

    /// <summary>
    /// Resolves an option by canonical spelling or alias, case-insensitively when the catalog declares
    /// <see cref="CommandCatalogBuilder.CaseInsensitive"/>.
    /// </summary>
    public bool TryGetOption(string token, out CommandOptionDescriptor? option)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _optionsByName.TryGetValue(token, out option);
    }

    // Resolves /name under the slash dialect, for an option declared as --name, -n or /name.
    internal bool TryGetSlashOption(string token, out CommandOptionDescriptor? option)
    {
        option = null;
        return _optionsBySlashName is not null && _optionsBySlashName.TryGetValue(token, out option);
    }

    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>([.. values]);
}
