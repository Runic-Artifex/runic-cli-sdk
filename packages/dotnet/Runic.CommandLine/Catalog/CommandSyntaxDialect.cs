namespace Runic.CommandLine;

/// <summary>
/// Selects how a catalog's commands, options and framework requests are spelled on the command line. Every runner,
/// help, usage hint and suggestion follows the catalog's dialect; see <see cref="CommandCatalogBuilder.Syntax"/>.
/// </summary>
/// <remarks>Open: a later minor release may add values, so code that switches over them must handle an unknown value.</remarks>
public enum CommandSyntaxDialect
{
    /// <summary>
    /// The portable grammar, the default: <c>tool config show --name value</c>, <c>--name=value</c>, <c>-n</c>,
    /// <c>--help</c> and <c>-h</c>, <c>--version</c>.
    /// </summary>
    Portable = 0,

    /// <summary>
    /// The Windows slash grammar: <c>tool /config /show /name value</c> or <c>/name=value</c>, <c>/?</c> and
    /// <c>/help</c>, <c>/version</c>. A command is <c>/word</c> or <c>word</c>; an option declared as <c>--name</c>,
    /// <c>-n</c> or <c>/name</c> is <c>/name</c> or <c>/n</c>. The portable spellings keep working, <c>--</c> still ends
    /// options, and a token that starts with <c>/</c> but spells no command or option is data.
    /// </summary>
    WindowsSlash = 1,
}

/// <summary>Selects the order in which help lists commands; see <see cref="CommandCatalogBuilder.HelpOrder"/>.</summary>
/// <remarks>Open: a later minor release may add values, so code that switches over them must handle an unknown value.</remarks>
public enum CommandHelpOrder
{
    /// <summary>
    /// The order of <see cref="CommandCatalog.Commands"/> and <see cref="CommandDescriptor.Subcommands"/>, the default:
    /// registration order. A generated catalog registers its commands by name.
    /// </summary>
    Registration = 0,

    /// <summary>
    /// The order in which the commands were declared: the order <see cref="CommandCatalogBuilder.DeclarationOrder"/>
    /// records, which for a generated catalog is source order, then registration order. A group comes where its first
    /// declared command does.
    /// </summary>
    Declaration = 1,
}
