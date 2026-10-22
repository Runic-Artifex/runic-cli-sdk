using System;
using System.Collections.Generic;

namespace Runic.CommandLine;

/// <summary>An immutable, thread-safe collection of command definitions.</summary>
public sealed class CommandCatalog
{
    /// <summary>The output selector of a catalog that does not declare one.</summary>
    internal const string DefaultOutputSelector = "--format";

    private readonly Dictionary<string, CommandDescriptor> _commandsByName;
    private readonly OutputSelectorDeclaration? _outputSelector;

    internal CommandCatalog(
        IReadOnlyList<CommandDescriptor> commands,
        CommandDescriptor? defaultCommand = null,
        string? jsonOutputAlias = null,
        JsonOutputShape jsonOutputAliasShape = JsonOutputShape.Envelope,
        bool defaultCommandIsRoot = false,
        string? completionOption = null,
        OutputSelectorDeclaration? outputSelector = null,
        bool responseFiles = false)
    {
        Commands = commands;
        ResponseFiles = responseFiles;
        _outputSelector = outputSelector;
        CompletionOption = completionOption;
        DefaultCommand = defaultCommand;
        RootCommand = defaultCommandIsRoot ? defaultCommand : null;
        JsonOutputAlias = jsonOutputAlias;
        JsonOutputAliasShape = jsonOutputAliasShape;
        _commandsByName = new Dictionary<string, CommandDescriptor>(StringComparer.Ordinal);
        foreach (CommandDescriptor command in commands)
        {
            // The root command has no spelling: its name is only its identity in output.
            if (ReferenceEquals(command, RootCommand)) continue;
            _commandsByName.Add(command.Name, command);
            foreach (string alias in command.Aliases)
            {
                _commandsByName.Add(alias, command);
            }
        }
    }

    /// <summary>Gets root commands in registration order.</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; }

    /// <summary>Gets the command selected when the first token is not a root command.</summary>
    public CommandDescriptor? DefaultCommand { get; }

    /// <summary>
    /// Gets the application's unnamed root command, or null. It is also the <see cref="DefaultCommand"/>, but its
    /// name is not a command spelling, help does not list it, and the application's help is its help. See
    /// <see cref="CommandCatalogBuilder.RootCommand(string)"/>.
    /// </summary>
    public CommandDescriptor? RootCommand { get; }

    /// <summary>
    /// Gets the long option that selects human or JSON output, <c>--format</c> unless the application declared
    /// another with <see cref="CommandCatalogBuilder.OutputSelector(string, IEnumerable{string})"/>.
    /// </summary>
    public string OutputSelector => _outputSelector?.Name ?? DefaultOutputSelector;

    /// <summary>
    /// Gets the selector's hidden aliases: former spellings that parse exactly like <see cref="OutputSelector"/>
    /// but that help and completion do not show. Empty unless the application declared some with
    /// <see cref="CommandCatalogBuilder.OutputSelector(string, IEnumerable{string})"/>.
    /// </summary>
    public IReadOnlyList<string> OutputSelectorAliases => _outputSelector?.HiddenAliases ?? [];

    internal OutputSelectorDeclaration? DeclaredOutputSelector => _outputSelector;

    /// <summary>
    /// Gets the application's flag that stands for the output selector with the value <c>json</c>, such as
    /// <c>--json</c> for <c>--format json</c>, or null. See <see cref="CommandCatalogBuilder.JsonOutputAlias(string)"/>.
    /// </summary>
    public string? JsonOutputAlias { get; }

    /// <summary>
    /// Gets what <see cref="JsonOutputAlias"/> writes: the <c>runic.commandline/1</c> envelope, as the output
    /// selector does, or the bare payload. See <see cref="CommandCatalogBuilder.JsonOutputAlias(string, JsonOutputShape)"/>.
    /// </summary>
    public JsonOutputShape JsonOutputAliasShape { get; }

    /// <summary>
    /// Gets the application's option that writes a shell completion script, such as <c>--completions</c> for
    /// <c>tool --completions bash</c>, or null when the built-in <c>completion</c> command does. See
    /// <see cref="CommandCatalogBuilder.CompletionOption(string)"/>.
    /// </summary>
    public string? CompletionOption { get; }

    /// <summary>
    /// Gets whether an argument that starts with <c>@</c>, such as <c>tool @build.rsp</c>, names a response file
    /// whose arguments replace it. The application declares it with <see cref="CommandCatalogBuilder.ResponseFiles"/>;
    /// otherwise, the default, <c>@build.rsp</c> is an ordinary argument. A catalog combined with
    /// <see cref="Combine"/> reads response files when one of its parts does.
    /// </summary>
    public bool ResponseFiles { get; }

    // The built-in completion command exists unless the application declared a completion option or owns the name.
    internal bool HasCompletionCommand => CompletionOption is null && !_commandsByName.ContainsKey("completion");

    /// <summary>Combines independently created catalogs in the supplied order.</summary>
    /// <remarks>
    /// This is the composition point for generated and explicitly registered
    /// commands. It preserves each catalog's immutable registrations and
    /// rejects duplicate root spellings deterministically.
    /// </remarks>
    public static CommandCatalog Combine(params CommandCatalog[] catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var commands = new List<CommandDescriptor>();
        var spellings = new Dictionary<string, string>(StringComparer.Ordinal);
        var issues = new List<CommandCatalogIssue>();
        CommandDescriptor? defaultCommand = null;
        bool defaultCommandIsRoot = false;
        string? jsonOutputAlias = null;
        JsonOutputShape jsonOutputAliasShape = JsonOutputShape.Envelope;
        string? completionOption = null;
        OutputSelectorDeclaration? outputSelector = null;
        bool responseFiles = false;
        foreach (CommandCatalog catalog in catalogs)
        {
            ArgumentNullException.ThrowIfNull(catalog);
            responseFiles |= catalog.ResponseFiles;
            if (catalog.DeclaredOutputSelector is { } declaredSelector)
            {
                if (outputSelector is not null && !outputSelector.Equals(declaredSelector))
                {
                    issues.Add(new CommandCatalogIssue(
                        "RCLI0026",
                        "root",
                        $"Combined catalogs declare different output selectors: {outputSelector} and {declaredSelector}."));
                }

                outputSelector ??= declaredSelector;
            }

            if (catalog.CompletionOption is { } declaredOption)
            {
                if (completionOption is not null && !string.Equals(completionOption, declaredOption, StringComparison.Ordinal))
                {
                    issues.Add(new CommandCatalogIssue(
                        "RCLI0025",
                        "root",
                        $"Combined catalogs declare different completion options: '{completionOption}' and '{declaredOption}'."));
                }

                completionOption ??= declaredOption;
            }

            if (catalog.JsonOutputAlias is { } declaredAlias)
            {
                if (jsonOutputAlias is not null && !string.Equals(jsonOutputAlias, declaredAlias, StringComparison.Ordinal))
                {
                    issues.Add(new CommandCatalogIssue(
                        "RCLI0024",
                        "root",
                        $"Combined catalogs declare different JSON output aliases: '{jsonOutputAlias}' and '{declaredAlias}'."));
                }
                else if (jsonOutputAlias is not null && jsonOutputAliasShape != catalog.JsonOutputAliasShape)
                {
                    issues.Add(new CommandCatalogIssue(
                        "RCLI0024",
                        "root",
                        $"Combined catalogs declare the JSON output alias '{declaredAlias}' with different output shapes."));
                }

                if (jsonOutputAlias is null)
                {
                    jsonOutputAlias = declaredAlias;
                    jsonOutputAliasShape = catalog.JsonOutputAliasShape;
                }
            }

            if (catalog.DefaultCommand is not null)
            {
                if (defaultCommand is not null)
                {
                    issues.Add(new CommandCatalogIssue("RCLI0019", "root", "Only one combined catalog may define a default command."));
                }
                else
                {
                    defaultCommand = catalog.DefaultCommand;
                    defaultCommandIsRoot = catalog.RootCommand is not null;
                }
            }
            foreach (CommandDescriptor command in catalog.Commands)
            {
                Register(command.Name, command.Name);
                foreach (string alias in command.Aliases) Register(alias, command.Name);
                commands.Add(command);
            }
        }

        if (issues.Count == 0) ValidateFrameworkOptions(outputSelector, jsonOutputAlias, completionOption, commands, issues);
        if (issues.Count != 0) throw new CommandCatalogValidationException(issues);
        return new CommandCatalog(CommandDescriptor.Freeze(commands), defaultCommand, jsonOutputAlias, jsonOutputAliasShape, defaultCommandIsRoot, completionOption, outputSelector, responseFiles);

        void Register(string spelling, string owner)
        {
            if (!spellings.TryAdd(spelling, owner))
            {
                issues.Add(new CommandCatalogIssue(
                    "RCLI0004",
                    "root",
                    $"Command spelling '{spelling}' is registered more than once."));
            }
        }
    }

    // The framework options (output selector, its hidden aliases, the JSON output alias and the completion option)
    // are recognized before any command option, so no option may share a spelling with them, nor they with each other.
    internal static void ValidateFrameworkOptions(
        OutputSelectorDeclaration? outputSelector,
        string? jsonOutputAlias,
        string? completionOption,
        IReadOnlyList<CommandDescriptor> commands,
        List<CommandCatalogIssue> issues)
    {
        string selector = outputSelector?.Name ?? DefaultOutputSelector;
        IReadOnlyList<string> selectorAliases = outputSelector?.HiddenAliases ?? [];
        bool IsSelectorSpelling(string spelling) =>
            string.Equals(spelling, selector, StringComparison.Ordinal) || System.Linq.Enumerable.Contains(selectorAliases, spelling, StringComparer.Ordinal);

        if (jsonOutputAlias is not null && IsSelectorSpelling(jsonOutputAlias))
        {
            issues.Add(new CommandCatalogIssue(
                "RCLI0024",
                "root",
                $"The JSON output alias '{jsonOutputAlias}' is spelled like the output selector '{selector}' or one of its aliases."));
        }

        if (completionOption is not null && (IsSelectorSpelling(completionOption) || string.Equals(completionOption, jsonOutputAlias, StringComparison.Ordinal)))
        {
            issues.Add(new CommandCatalogIssue(
                "RCLI0025",
                "root",
                $"The completion option '{completionOption}' is spelled like the output selector, one of its aliases or the JSON output alias."));
        }

        Visit(commands, "");
        void Visit(IReadOnlyList<CommandDescriptor> level, string parentPath)
        {
            foreach (CommandDescriptor command in level)
            {
                string path = parentPath.Length == 0 ? command.Name : parentPath + " " + command.Name;
                foreach (CommandOptionDescriptor option in command.Options)
                {
                    foreach (string spelling in System.Linq.Enumerable.Prepend(option.Aliases, option.Name))
                    {
                        if (IsSelectorSpelling(spelling))
                        {
                            issues.Add(new CommandCatalogIssue(
                                "RCLI0026",
                                path,
                                $"Option '{option.Id}' of command '{path}' is spelled like the output selector '{selector}'" +
                                (string.Equals(spelling, selector, StringComparison.Ordinal) ? "." : $" alias '{spelling}'.") +
                                " Declare another selector with CommandCatalogBuilder.OutputSelector, or rename the option."));
                        }
                        else if (string.Equals(spelling, jsonOutputAlias, StringComparison.Ordinal))
                        {
                            issues.Add(new CommandCatalogIssue(
                                "RCLI0024",
                                path,
                                $"Option '{option.Id}' of command '{path}' is spelled like the JSON output alias '{jsonOutputAlias}'."));
                        }
                        else if (string.Equals(spelling, completionOption, StringComparison.Ordinal))
                        {
                            issues.Add(new CommandCatalogIssue(
                                "RCLI0025",
                                path,
                                $"Option '{option.Id}' of command '{path}' is spelled like the completion option '{completionOption}'."));
                        }
                    }
                }

                Visit(command.Subcommands, path);
            }
        }
    }

    /// <summary>Resolves a root command by canonical name or alias.</summary>
    public bool TryGetCommand(string token, out CommandDescriptor? command)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _commandsByName.TryGetValue(token, out command);
    }

    /// <summary>Resolves the longest leading command path.</summary>
    /// <param name="tokens">Already-tokenized command-line arguments.</param>
    /// <param name="command">The deepest resolved command, when any command matched.</param>
    /// <param name="consumed">The number of consumed command tokens.</param>
    /// <returns>True when at least one command matched.</returns>
    public bool TryResolve(ReadOnlySpan<string> tokens, out CommandDescriptor? command, out int consumed)
    {
        command = null;
        consumed = 0;
        if (tokens.IsEmpty || !_commandsByName.TryGetValue(tokens[0], out CommandDescriptor? current))
        {
            return false;
        }

        command = current;
        consumed = 1;
        while (consumed < tokens.Length && current!.TryGetSubcommand(tokens[consumed], out CommandDescriptor? child))
        {
            current = child;
            command = current;
            consumed++;
        }

        return true;
    }
}

/// <summary>An application's declared output selector and its hidden aliases.</summary>
internal sealed record OutputSelectorDeclaration(string Name, IReadOnlyList<string> HiddenAliases)
{
    public bool Equals(OutputSelectorDeclaration? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal) &&
        System.Linq.Enumerable.SequenceEqual(
            System.Linq.Enumerable.Order(HiddenAliases, StringComparer.Ordinal),
            System.Linq.Enumerable.Order(other.HiddenAliases, StringComparer.Ordinal),
            StringComparer.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    public override string ToString() =>
        HiddenAliases.Count == 0 ? "'" + Name + "'" : "'" + Name + "' (aliases '" + string.Join("', '", HiddenAliases) + "')";
}
