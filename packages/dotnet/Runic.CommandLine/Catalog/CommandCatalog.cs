using System;
using System.Collections.Generic;

namespace Runic.CommandLine;

/// <summary>An immutable, thread-safe collection of command definitions.</summary>
public sealed class CommandCatalog
{
    private readonly Dictionary<string, CommandDescriptor> _commandsByName;

    internal CommandCatalog(
        IReadOnlyList<CommandDescriptor> commands,
        CommandDescriptor? defaultCommand = null,
        string? jsonOutputAlias = null,
        JsonOutputShape jsonOutputAliasShape = JsonOutputShape.Envelope,
        bool defaultCommandIsRoot = false)
    {
        Commands = commands;
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
    /// Gets the application's flag that stands for the output selector with the value <c>json</c>, such as
    /// <c>--json</c> for <c>--output json</c>, or null. See <see cref="CommandCatalogBuilder.JsonOutputAlias(string)"/>.
    /// </summary>
    public string? JsonOutputAlias { get; }

    /// <summary>
    /// Gets what <see cref="JsonOutputAlias"/> writes: the <c>runic.commandline/1</c> envelope, as the output
    /// selector does, or the bare payload. See <see cref="CommandCatalogBuilder.JsonOutputAlias(string, JsonOutputShape)"/>.
    /// </summary>
    public JsonOutputShape JsonOutputAliasShape { get; }

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
        foreach (CommandCatalog catalog in catalogs)
        {
            ArgumentNullException.ThrowIfNull(catalog);
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

        if (jsonOutputAlias is not null) ValidateJsonOutputAlias(jsonOutputAlias, commands, "", issues);
        if (issues.Count != 0) throw new CommandCatalogValidationException(issues);
        return new CommandCatalog(CommandDescriptor.Freeze(commands), defaultCommand, jsonOutputAlias, jsonOutputAliasShape, defaultCommandIsRoot);

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

    // The alias is recognized before any command option, so no option may be spelled like it.
    internal static void ValidateJsonOutputAlias(
        string alias,
        IReadOnlyList<CommandDescriptor> commands,
        string parentPath,
        List<CommandCatalogIssue> issues)
    {
        foreach (CommandDescriptor command in commands)
        {
            string path = parentPath.Length == 0 ? command.Name : parentPath + " " + command.Name;
            foreach (CommandOptionDescriptor option in command.Options)
            {
                if (string.Equals(option.Name, alias, StringComparison.Ordinal) || System.Linq.Enumerable.Contains(option.Aliases, alias, StringComparer.Ordinal))
                {
                    issues.Add(new CommandCatalogIssue(
                        "RCLI0024",
                        path,
                        $"Option '{option.Id}' of command '{path}' is spelled like the JSON output alias '{alias}'."));
                }
            }

            ValidateJsonOutputAlias(alias, command.Subcommands, path, issues);
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
