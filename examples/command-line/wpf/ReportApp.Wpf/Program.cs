using System.Windows;
using Runic.CommandLine.Examples.ReportApp;
using Runic.CommandLine.Hosting;

namespace Runic.CommandLine.Examples.ReportApp.Wpf;

/// <summary>
/// Classifies the launch before WPF starts. A WinExe has no console, so commands are not run here:
/// they belong to the sibling console executable (reportcli). Only a bare launch opens the window.
/// </summary>
public static class Program
{
    /// <summary>The process entry point.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (ReportCommandApp.Classify(args) != HostedCommandLineDecisionKind.UserInterface)
        {
            // Nothing can be written to a stdout that does not exist, so tell the user in a dialog.
            MessageBox.Show("This is the graphical application. Run commands with reportcli.exe, for example: reportcli items list",
                "Inventory", MessageBoxButton.OK, MessageBoxImage.Information);
            return 64;
        }

        var app = new App();
        return app.Run();
    }
}
