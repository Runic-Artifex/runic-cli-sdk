using System.Globalization;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;

namespace Runic.CommandLine.Examples.ReportApp;

/// <summary>Command handlers: thin adapters over <see cref="IReportService"/>.</summary>
internal static class ReportCommands
{
    [Command("items list", Description = "List inventory items.", Examples = ["reportcli items list --max-quantity 10"])]
    internal static async Task<string> ListItems([FromServices] IReportService reports,
        [Option("--max-quantity", Description = "Only items at or below this quantity.", Minimum = 0)] int? maxQuantity = null,
        CancellationToken cancellationToken = default)
    {
        var items = await reports.ListItemsAsync(maxQuantity, cancellationToken);
        return string.Join('\n', items.Select(item => $"{item.Name}: {item.Quantity}"));
    }

    [Command("report export", Description = "Export the inventory report as CSV.")]
    internal static async Task<string> Export([FromServices] IReportService reports,
        [Argument(Description = "Destination CSV path.")] string path,
        [Option("--overwrite", Description = "Replace an existing file.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var result = await reports.ExportAsync(path, overwrite, cancellationToken);
        return string.Create(CultureInfo.InvariantCulture, $"Exported {result.Rows} rows to {result.Path}");
    }
}

/// <summary>Creates the command application and the launch classifier from the application's own services.</summary>
public static class ReportCommandApp
{
    /// <summary>Creates a runner whose handlers receive <paramref name="reports"/> through <c>[FromServices]</c>.</summary>
    public static CommandApp Create(IReportService reports, ICommandConsole console) => new(GeneratedCommandCatalog.Create())
    {
        // The application owns the service; each invocation only resolves it.
        ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With(reports)),
        Name = "reportcli", Version = "1.0.0", Console = console,
    };

    /// <summary>
    /// Decides, before any window exists, whether a launch is a command or the normal UI. Only a known command,
    /// help, version or completion request (or a known command with invalid arguments) is a command. Everything
    /// else, including a document path from a file association, starts the UI. The trade-off: a mistyped
    /// command name opens the window instead of reporting an error.
    /// </summary>
    public static ReportLaunchMode Classify(IReadOnlyList<string> args)
    {
        // Classification runs no handler, so it needs no services.
        var adapter = new CommandLineHostingAdapter(new CommandApp(GeneratedCommandCatalog.Create()));
        var decision = adapter.Classify(new HostedCommandLineLaunchInput(args, emptyInputFallback: EmptyInputFallback.UserInterface));
        return decision.IsCommandLineRequest ? ReportLaunchMode.Command : ReportLaunchMode.UserInterface;
    }
}

/// <summary>What a launch of the GUI executable should do.</summary>
public enum ReportLaunchMode
{
    /// <summary>Start the window; the application handles its own arguments.</summary>
    UserInterface,

    /// <summary>A command-line request, served by the console executable.</summary>
    Command,
}
