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

    // A defaulted CancellationToken parameter is rejected by the generator (RCLI9022), and a non-defaulted
    // one cannot follow the defaulted --overwrite option, so the token precedes the argument and options.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "The generator rejects a defaulted token (RCLI9022).")]
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

    private static readonly string[] CommandRoots = ["items", "report"];

    /// <summary>
    /// Decides, before any window exists, whether a launch is a command or the normal UI. Only a known command,
    /// help, version or completion request (or a known command with invalid arguments) is a command. Everything
    /// else, including a document path from a file association, starts the UI. The trade-off: a mistyped
    /// command name opens the window instead of reporting an error.
    /// </summary>
    public static ReportLaunchMode Classify(IReadOnlyList<string> args)
    {
        var adapter = new CommandLineHostingAdapter(GeneratedCommandCatalog.Create(), new CommandExecutor(new ReportScopes(null)));
        var kind = adapter.Classify(new HostedCommandLineLaunchInput(args, emptyInputFallback: EmptyInputFallback.UserInterface)).Kind;
        return kind switch
        {
            HostedCommandLineDecisionKind.Invocation or HostedCommandLineDecisionKind.Help
                or HostedCommandLineDecisionKind.Version or HostedCommandLineDecisionKind.Completion => ReportLaunchMode.Command,
            HostedCommandLineDecisionKind.Invalid when Array.IndexOf(CommandRoots, args[0]) >= 0 => ReportLaunchMode.Command,
            _ => ReportLaunchMode.UserInterface,
        };
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

/// <summary>What a launch of the GUI executable should do.</summary>
public enum ReportLaunchMode
{
    /// <summary>Start the window; the application handles its own arguments.</summary>
    UserInterface,

    /// <summary>A command-line request, served by the console executable.</summary>
    Command,
}
