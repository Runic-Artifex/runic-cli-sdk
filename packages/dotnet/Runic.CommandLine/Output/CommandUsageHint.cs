namespace Runic.CommandLine;

// The lines a usage error ends with in human output: the usage line of the command the error belongs to, as help
// writes it, and the help command to run. They are presentation text only; JSON output never contains them.
internal sealed class CommandUsageHint
{
    private readonly CommandCatalog _catalog;
    private readonly string _applicationName;

    internal CommandUsageHint(CommandCatalog catalog, string applicationName)
    {
        _catalog = catalog;
        _applicationName = applicationName;
    }

    internal string Format(CommandPath path, CommandTextContext context, ICommandConsole console)
    {
        string usage = CommandHelpFormatter.FormatUsage(_catalog, _applicationName, path, context,
            CommandPresentation.HelpWidth(console, CommandOutputMode.Human));
        string help = _applicationName + (path.Count > 0 ? " " + path : "") + " --help";
        string english = "Run \"" + help + "\" for more information.";
        string run = context.Resolve(CommandTextKeys.HintUsage, english, [help]);
        // Resolved text passes the sanitizer that diagnostic messages pass.
        run = CommandFaultSanitizer.ContainsTechnicalContent(run) ? english : CommandFaultSanitizer.SanitizeRequiredText(run);
        return usage + run + "\n";
    }
}
