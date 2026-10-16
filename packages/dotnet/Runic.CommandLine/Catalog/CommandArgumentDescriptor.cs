namespace Runic.CommandLine;

/// <summary>Describes a parser-neutral positional command argument.</summary>
public sealed class CommandArgumentDescriptor
{
    internal CommandArgumentDescriptor(
        string id,
        string name,
        CommandArity arity,
        bool isSensitive,
        string? descriptionKey,
        CommandHelp? help = null,
        bool isTrailing = false)
    {
        IsTrailing = isTrailing;
        Id = id;
        Name = name;
        Arity = arity;
        IsSensitive = isSensitive;
        DescriptionKey = descriptionKey;
        Help = help ?? CommandHelp.Empty;
    }

    /// <summary>Gets shared help and value metadata.</summary>
    public CommandHelp Help { get; }

    /// <summary>Gets the stable parameter identifier used by binders.</summary>
    public string Id { get; }

    /// <summary>Gets the help-facing argument name.</summary>
    public string Name { get; }

    /// <summary>Gets the accepted value count.</summary>
    public CommandArity Arity { get; }

    /// <summary>Gets whether values must be redacted from diagnostics.</summary>
    public bool IsSensitive { get; }

    /// <summary>Gets the optional localization key for help text.</summary>
    public string? DescriptionKey { get; }

    /// <summary>
    /// Gets whether the argument receives every token after <c>--</c> instead of positional tokens. A command has
    /// at most one such argument, it is the last of <see cref="CommandDescriptor.Arguments"/>, and positional
    /// arguments then receive no token after <c>--</c>.
    /// </summary>
    public bool IsTrailing { get; }
}
