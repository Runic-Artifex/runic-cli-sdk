using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace Runic.CommandLine.Spectre;

/// <summary>Displays catalog help with terminal-aware Spectre styling.</summary>
public sealed class SpectreHelpPresenter : ICommandHelpPresenter
{
    /// <inheritdoc />
    public async ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path,
        CommandTextContext textContext, ICommandConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        var presenter = console as SpectreCommandConsole ?? new SpectreCommandConsole(console);
        // Wrap to the presenter width so Spectre never rewraps a line and loses the description column. For a measured
        // terminal width, stay one column short so a full line never triggers the legacy Windows console's automatic
        // wrap; an explicit width is used as given.
        int width = presenter.IsTerminalWidth ? Math.Max(CommandHelpFormatter.MinimumWidth, presenter.Width - 1) : presenter.Width;
        string text = CommandHelpFormatter.Format(catalog, applicationName, path, textContext, width);
        // Headings are found by the text the formatter resolved for them in this culture, never by the English words:
        // the usage line starts with the help.usage label, and a section heading is its label alone on a line.
        CommandTextContext labels = textContext ?? CommandTextContext.English;
        string usagePrefix = labels.Resolve(CommandTextKeys.HelpUsage, "Usage") + ": ";
        var headings = new HashSet<string>(StringComparer.Ordinal)
        {
            labels.Resolve(CommandTextKeys.HelpCommands, "Commands") + ":",
            labels.Resolve(CommandTextKeys.HelpArguments, "Arguments") + ":",
            labels.Resolve(CommandTextKeys.HelpOptions, "Options") + ":",
            labels.Resolve(CommandTextKeys.HelpExitCodes, "Exit codes") + ":",
            labels.Resolve(CommandTextKeys.HelpExamples, "Examples") + ":",
        };
        // Keep the plain and styled forms textually equivalent, including under redirection.
        string[] lines = text.TrimEnd('\n').Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            bool heading = (index == 0 && line.StartsWith(usagePrefix, StringComparison.Ordinal)) || headings.Contains(line);
            await presenter.WriteAsync(new Text(line + "\n", heading ? new Style(foreground: Color.Cyan, decoration: Decoration.Bold) : Style.Plain), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
