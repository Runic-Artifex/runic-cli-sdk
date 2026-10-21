using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Renders catalog help for a person. Machine help uses the shared framework presentation layer.</summary>
public interface ICommandHelpPresenter
{
    /// <summary>Writes help to an invocation-local console.</summary>
    /// <param name="catalog">The command catalog; <see cref="CommandCatalog.OutputSelector"/> names the output selector.</param>
    /// <param name="applicationName">The name shown in the usage line.</param>
    /// <param name="path">The root or a resolved command path.</param>
    /// <param name="textContext">The invocation's culture and text resolution.</param>
    /// <param name="console">The invocation-local console.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path,
        CommandTextContext textContext, ICommandConsole console, CancellationToken cancellationToken);
}
