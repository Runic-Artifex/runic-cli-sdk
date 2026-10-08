using System.Windows;
using Runic.CommandLine.Examples.ReportApp;

namespace Runic.CommandLine.Examples.ReportApp.Wpf;

/// <summary>The existing WPF application. It builds its services with the same helper the CLI uses.</summary>
public sealed class App : Application
{
    /// <summary>Gets this process's service instance. The CLI process builds its own from the same helper.</summary>
    public IReportService Reports { get; } = ReportComposition.CreateReportService();

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow { DataContext = new MainViewModel(Reports) };
        // A file association, "Open with" or drag-drop passes the document path as the first argument.
        if (e.Args.Length > 0) window.Title = $"Inventory - {e.Args[0]}";
        window.Show();
    }
}
