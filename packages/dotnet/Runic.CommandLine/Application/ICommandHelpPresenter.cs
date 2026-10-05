using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Renders catalog help for a person. Machine help uses the shared framework presentation layer.</summary>
public interface ICommandHelpPresenter
{
    /// <summary>Writes help to an invocation-local console.</summary>
    ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName, ICommandConsole console, CancellationToken cancellationToken);

    /// <summary>Writes help with explicit culture and text resolution. Existing presenters retain their original behavior.</summary>
    ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName,
        CommandTextContext textContext, ICommandConsole console, CancellationToken cancellationToken) =>
        WriteAsync(catalog, applicationName, path, outputOptionName, console, cancellationToken);
}
