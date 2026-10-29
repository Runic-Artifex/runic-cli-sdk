using System;
using System.Collections.Generic;

namespace Runic.CommandLine;

/// <summary>Marks a static method as a command handled by the built-in source generator.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CommandAttribute : Attribute
{
    /// <summary>Initializes a command with its canonical catalog name.</summary>
    public CommandAttribute(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Gets the canonical command name.</summary>
    public string Name { get; }
    /// <summary>Gets or sets whether this entry is hidden from discovery.</summary>
    public bool Hidden { get; set; }
    /// <summary>Gets or sets the localization key for descriptive help; Description supplies its fallback.</summary>
    public string? DescriptionKey { get; set; }
    /// <summary>Gets or sets descriptive help.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets extended command help.</summary>
    public string? LongDescription { get; set; }
    /// <summary>Gets or sets runnable examples.</summary>
    public string[] Examples { get; set; } = [];
}

/// <summary>
/// Describes a help-only group that the generator creates for nested command names, such as <c>config</c>
/// for <c>[Command("config show")]</c>. Apply it to any class that declares commands.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CommandGroupAttribute : Attribute
{
    /// <summary>Initializes a description for the group at a space-separated path.</summary>
    public CommandGroupAttribute(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>Gets the group's space-separated path, a leading part of at least one command name.</summary>
    public string Path { get; }
    /// <summary>Gets or sets whether this group is hidden from discovery.</summary>
    public bool Hidden { get; set; }
    /// <summary>Gets or sets the localization key for descriptive help; Description supplies its fallback.</summary>
    public string? DescriptionKey { get; set; }
    /// <summary>Gets or sets descriptive help.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets extended group help.</summary>
    public string? LongDescription { get; set; }
}

/// <summary>
/// Declares the long option that selects human or JSON output for the generated catalog, replacing the default
/// <c>--format</c>, and optionally hidden aliases that keep former spellings working:
/// <c>[assembly: OutputSelector("--output-format")]</c> leaves <c>--format</c> to the application's own commands.
/// </summary>
/// <remarks>
/// <para>
/// <c>GeneratedCommandCatalog.Create</c> declares the selector with
/// <see cref="CommandCatalogBuilder.OutputSelector(string, IEnumerable{string})"/> before it calls its
/// <c>configure</c> callback, which may still declare another. Unlike a selector declared only in that callback, the
/// generator sees this one: it reports <c>RCLI9045</c> for an option spelled like the declared selector or one of its
/// aliases rather than like <c>--format</c>, and <c>RCLI9046</c> for a spelling the builder would reject.
/// </para>
/// <para>
/// The spellings follow the rules of <see cref="CommandCatalogBuilder.OutputSelector(string, IEnumerable{string})"/>:
/// long option spellings (<c>--</c>, a lowercase letter, then lowercase letters, digits or hyphens) other than
/// <c>--help</c> and <c>--version</c>, each given once.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class OutputSelectorAttribute : Attribute
{
    /// <summary>Initializes the declaration.</summary>
    /// <param name="name">A long option spelling such as <c>--output-format</c>.</param>
    /// <param name="hiddenAliases">Further spellings that parse exactly like the selector and are not shown.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="hiddenAliases"/> is null.</exception>
    public OutputSelectorAttribute(string name, params string[] hiddenAliases)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ArgumentNullException.ThrowIfNull(hiddenAliases);
        HiddenAliases = Array.AsReadOnly((string[])hiddenAliases.Clone());
    }

    /// <summary>Gets the selector's spelling.</summary>
    public string Name { get; }

    /// <summary>Gets the hidden aliases of the selector.</summary>
    public IReadOnlyList<string> HiddenAliases { get; }
}

/// <summary>Marks one generated command as the root fallback for positional-only invocation.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DefaultCommandAttribute : Attribute
{
}

/// <summary>
/// Marks one generated command as the application's unnamed root command: the default command whose name is not a
/// command spelling and whose help is the application's help. See <see cref="CommandCatalogBuilder.RootCommand(string)"/>.
/// </summary>
/// <remarks>
/// The <see cref="CommandAttribute"/> name stays the command's identity in JSON output. Use it instead of
/// <see cref="DefaultCommandAttribute"/>; one command in a catalog can carry either.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RootCommandAttribute : Attribute
{
}

/// <summary>Marks a parameter as a positional command argument.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ArgumentAttribute : Attribute
{
    /// <summary>Gets or sets whether this entry is hidden from discovery.</summary>
    public bool Hidden { get; set; }
    /// <summary>Gets or sets the localization key for descriptive help; Description supplies its fallback.</summary>
    public string? DescriptionKey { get; set; }
    /// <summary>Gets or sets descriptive help.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets path completion and validation metadata.</summary>
    public CommandPathKind PathKind { get; set; }
    /// <summary>Gets or sets whether a path must exist before the handler runs.</summary>
    public bool MustExist { get; set; }
    /// <summary>Gets or sets the inclusive numeric minimum; NaN leaves it unspecified.</summary>
    public double Minimum { get; set; } = double.NaN;
    /// <summary>Gets or sets the inclusive numeric maximum; NaN leaves it unspecified.</summary>
    public double Maximum { get; set; } = double.NaN;
    /// <summary>Gets or sets the value placeholder.</summary>
    public string? ValueName { get; set; }
    /// <summary>Gets or sets the allowed values.</summary>
    public string[] Choices { get; set; } = [];
    /// <summary>Gets or sets whether values are sensitive.</summary>
    public bool Sensitive { get; set; }

    /// <summary>Initializes an argument using its parameter name as the stable identifier.</summary>
    public ArgumentAttribute()
    {
    }

    /// <summary>Initializes an argument with an explicit stable identifier.</summary>
    public ArgumentAttribute(string id) => Id = id ?? throw new ArgumentNullException(nameof(id));

    /// <summary>Gets the optional stable argument identifier.</summary>
    public string? Id { get; }

    /// <summary>
    /// Gets or sets whether this trailing argument accepts all remaining
    /// positional values.
    /// </summary>
    /// <remarks>
    /// This is supported only for a trailing generated
    /// <c>IReadOnlyList&lt;string&gt;</c> argument. The default is one value.
    /// </remarks>
    public bool AllowMultipleValues { get; set; }

    /// <summary>Gets or sets whether this argument must be supplied.</summary>
    /// <remarks>
    /// A required list argument (<see cref="AllowMultipleValues"/>) needs at least one value. A single argument is
    /// already required unless its type is nullable or it declares a C# default; <c>Required</c> makes a nullable
    /// one required too. A required argument cannot declare a C# default (<c>RCLI9023</c>), and cannot follow an
    /// optional argument (<c>RCLI9035</c>).
    /// </remarks>
    public bool Required { get; set; }
}

/// <summary>
/// Marks a list parameter that receives every token after <c>--</c>, such as native arguments a command forwards to
/// another tool. Positional arguments then receive only the tokens before <c>--</c>.
/// </summary>
/// <remarks>
/// The parameter is an array or <c>IReadOnlyList&lt;T&gt;</c> of a supported value type and is empty when the command
/// line has no <c>--</c> or nothing after it. A command has at most one. See
/// <c>CommandBuilder.TrailingArguments</c> for catalogs built by hand.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class TrailingArgumentsAttribute : Attribute
{
    /// <summary>Initializes trailing arguments using the parameter name as the stable identifier.</summary>
    public TrailingArgumentsAttribute()
    {
    }

    /// <summary>Initializes trailing arguments with an explicit stable identifier.</summary>
    public TrailingArgumentsAttribute(string id) => Id = id ?? throw new ArgumentNullException(nameof(id));

    /// <summary>Gets the optional stable identifier.</summary>
    public string? Id { get; }
    /// <summary>Gets or sets whether this entry is hidden from discovery.</summary>
    public bool Hidden { get; set; }
    /// <summary>Gets or sets the localization key for descriptive help; Description supplies its fallback.</summary>
    public string? DescriptionKey { get; set; }
    /// <summary>Gets or sets descriptive help.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets the value placeholder.</summary>
    public string? ValueName { get; set; }
    /// <summary>Gets or sets whether values are sensitive.</summary>
    public bool Sensitive { get; set; }
}

/// <summary>Marks a parameter as a named command option.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class OptionAttribute : Attribute
{
    /// <summary>Gets or sets an environment fallback used only when the option is absent.</summary>
    public string? EnvironmentVariable { get; set; }
    /// <summary>Gets or sets whether this entry is hidden from discovery.</summary>
    public bool Hidden { get; set; }
    /// <summary>Gets or sets the localization key for descriptive help; Description supplies its fallback.</summary>
    public string? DescriptionKey { get; set; }
    /// <summary>Gets or sets descriptive help.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets path completion and validation metadata.</summary>
    public CommandPathKind PathKind { get; set; }
    /// <summary>Gets or sets whether a path must exist before the handler runs.</summary>
    public bool MustExist { get; set; }
    /// <summary>Gets or sets the inclusive numeric minimum; NaN leaves it unspecified.</summary>
    public double Minimum { get; set; } = double.NaN;
    /// <summary>Gets or sets the inclusive numeric maximum; NaN leaves it unspecified.</summary>
    public double Maximum { get; set; } = double.NaN;
    /// <summary>Gets or sets the value placeholder.</summary>
    public string? ValueName { get; set; }
    /// <summary>Gets or sets the allowed values.</summary>
    public string[] Choices { get; set; } = [];
    /// <summary>Gets or sets whether values are sensitive.</summary>
    public bool Sensitive { get; set; }

    /// <summary>Gets or sets required companion option IDs.</summary>
    public string[] Requires { get; set; } = [];
    /// <summary>Gets or sets mutually exclusive option IDs.</summary>
    public string[] ConflictsWith { get; set; } = [];

    /// <summary>Initializes an option with its canonical spelling and optional aliases.</summary>
    public OptionAttribute(string name, params string[] aliases)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
    }

    /// <summary>Gets the canonical option spelling.</summary>
    public string Name { get; }

    /// <summary>Gets alternate option spellings.</summary>
    public string[] Aliases { get; }

    /// <summary>
    /// Gets or sets whether one occurrence may consume all following values up
    /// to the next option.
    /// </summary>
    /// <remarks>
    /// This is supported only for generated <c>IReadOnlyList&lt;string&gt;</c>
    /// parameters. The default is one value for each occurrence.
    /// </remarks>
    public bool AllowMultipleValues { get; set; }

    /// <summary>Gets or sets whether a list option accepts repeated occurrences.</summary>
    /// <remarks>
    /// This applies only to generated <c>IReadOnlyList&lt;string&gt;</c> options.
    /// The default preserves existing behavior: values from repeated occurrences
    /// are appended in encounter order.
    /// </remarks>
    public bool AllowMultipleOccurrences { get; set; } = true;

    /// <summary>Gets or sets whether this option must be supplied.</summary>
    /// <remarks>
    /// A required flag must be present. A required repeated option must have at
    /// least one valid occurrence.
    /// </remarks>
    public bool Required { get; set; }
}

/// <summary>Marks a parameter as an explicitly required invocation-scoped service.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromServicesAttribute : Attribute
{
}

/// <summary>Supplies the machine payload identity emitted for a generated command result.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CommandResultAttribute : Attribute
{
    /// <summary>Initializes result metadata with a stable payload identity and source-generated context.</summary>
    public CommandResultAttribute(string payloadType, Type jsonContextType)
    {
        PayloadType = payloadType ?? throw new ArgumentNullException(nameof(payloadType));
        JsonContextType = jsonContextType ?? throw new ArgumentNullException(nameof(jsonContextType));
    }

    /// <summary>Gets the stable payload identity.</summary>
    public string PayloadType { get; }

    /// <summary>Gets the application-owned source-generated JSON context type.</summary>
    public Type JsonContextType { get; }
}
