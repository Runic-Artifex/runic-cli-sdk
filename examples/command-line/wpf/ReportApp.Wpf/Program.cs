using System.Runtime.InteropServices;
using System.Windows;
using Runic.CommandLine.Examples.ReportApp;

namespace Runic.CommandLine.Examples.ReportApp.Wpf;

/// <summary>
/// Classifies the launch before WPF starts. A WinExe is not the place to run commands, so a command
/// launch only prints a hint and exits; the commands belong to the sibling console executable (reportcli).
/// Anything that is not a known command, including a document path, starts the window.
/// </summary>
public static class Program
{
    /// <summary>Exit code for a command launched on the GUI executable (sysexits EX_USAGE: wrong executable for this usage).</summary>
    public const int UsageExitCode = 64;

    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    /// <summary>The process entry point.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (ReportCommandApp.Classify(args) == ReportLaunchMode.Command)
        {
            const string hint = "This is the graphical application. Run commands with reportcli.exe, for example: reportcli items list";
            // Best effort and never modal in a script: write to the parent console if there is one.
            // The shell has usually already returned to its prompt, so this text can interleave with it.
            if (AttachConsole(AttachParentProcess))
                Console.Error.WriteLine(hint);
            else if (Environment.UserInteractive)
                MessageBox.Show(hint, "Inventory", MessageBoxButton.OK, MessageBoxImage.Information);
            return UsageExitCode;
        }

        // Application.Run exposes the command line as StartupEventArgs.Args in OnStartup.
        return new App().Run();
    }
}
