using System.Windows;
using Runic.CommandLine.Examples.ReportApp;

namespace Runic.CommandLine.Examples.ReportApp.Wpf;

/// <summary>The existing WPF application. It owns the services and hands them to its window.</summary>
public sealed class App : Application
{
    /// <summary>Gets the application service the window and, in the CLI, the commands share.</summary>
    public IReportService Reports { get; } = new ReportService();

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow { DataContext = new MainViewModel(Reports) }.Show();
    }
}
