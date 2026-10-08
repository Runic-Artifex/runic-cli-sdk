using System.Globalization;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;

namespace Runic.CommandLine.Examples.ReportApp;

/// <summary>Command handlers: thin adapters over <see cref="IReportService"/>.</summary>
internal static class ReportCommands
{
    [Command("items list", Description = "List inventory items.", Examples = ["reportcli items list --max-quantity 10"])]
    internal static async Task<string> ListItems([FromServices] IReportService reports, CancellationToken cancellationToken,
        [Option("--max-quantity", Description = "Only items at or below this quantity.", Minimum = 0)] int? maxQuantity = null)
    {
        var items = await reports.ListItemsAsync(maxQuantity, cancellationToken);
        return string.Join('\n', items.Select(item => $"{item.Name}: {item.Quantity}"));
    }

    // Defaulted parameters cannot precede the required argument, so the token stays ahead of it.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "The required argument and defaulted option must follow the token for generated binding.")]
    [Command("report export", Description = "Export the inventory report as CSV.")]
    internal static async Task<string> Export([FromServices] IReportService reports, CancellationToken cancellationToken,
        [Argument(Description = "Destination CSV path.")] string path,
        [Option("--overwrite", Description = "Replace an existing file.")] bool overwrite = false)
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
        ScopeFactory = new ReportScopes(reports), Name = "reportcli", Version = "1.0.0", Console = console,
    };

    /// <summary>
    /// Decides, before any window exists, whether a launch is a command or the normal UI. No arguments
    /// selects the UI; anything else is parsed as a command (help, version and errors included).
    /// </summary>
    public static HostedCommandLineDecisionKind Classify(IReadOnlyList<string> args)
    {
        var adapter = new CommandLineHostingAdapter(GeneratedCommandCatalog.Create(), new CommandExecutor(new ReportScopes(null)));
        return adapter.Classify(new HostedCommandLineLaunchInput(args, emptyInputFallback: EmptyInputFallback.UserInterface)).Kind;
    }

    private sealed class ReportScopes(IReportService? reports) : ICommandExecutionScopeFactory
    {
        public ICommandExecutionScope CreateScope() => new ReportScope(reports);
    }

    private sealed class ReportScope(IReportService? reports) : ICommandExecutionScope, IServiceProvider
    {
        public IServiceProvider Services => this;
        public object? GetService(Type type) => type == typeof(IReportService) ? reports : null;
        // The application owns its services; the scope owns nothing.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
