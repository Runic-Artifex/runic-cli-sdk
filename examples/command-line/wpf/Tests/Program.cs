using Runic.CommandLine.Examples.ReportApp;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

// The handlers run against the same IReportService the window uses; no process or window is started.
var app = new CommandAppTester(console => ReportCommandApp.Create(new ReportService(), console));

var all = await app.RunAsync(["items", "list"]);
if (all.ExitCode != 0 || !all.StandardOutput.Contains("Iron ingot: 40") || !all.StandardOutput.Contains("Rune stone: 3")) return 1;

var low = await app.RunAsync(["items", "list", "--max-quantity", "7"]);
if (low.ExitCode != 0 || low.StandardOutput.Contains("Iron ingot") || !low.StandardOutput.Contains("Oak plank: 7")) return 2;

var invalid = await app.RunAsync(["items", "list", "--max-quantity", "-1"]);
if (invalid.ExitCode != 2 || !invalid.StandardError.Contains("--max-quantity")) return 3;

var path = Path.Combine(Path.GetTempPath(), $"report-{Guid.NewGuid():N}.csv");
try
{
    var export = await app.RunAsync(["report", "export", path]);
    if (export.ExitCode != 0 || !export.StandardOutput.Contains("Exported 4 rows")) return 4;
    if (File.ReadAllText(path) != "name,quantity\nIron ingot,40\nOak plank,7\nRune stone,3\nSilver thread,12\n") return 5;

    var refused = await app.RunAsync(["report", "export", path]);
    if (refused.ExitCode == 0) return 6;
    var replaced = await app.RunAsync(["report", "export", path, "--overwrite"]);
    if (replaced.ExitCode != 0) return 7;
}
finally { File.Delete(path); }

var machine = await app.RunAsync(["items", "list", "--output=json"]);
using var frame = CommandTestEnvelope.Parse(machine.StandardOutput);
if (machine.ExitCode != 0 || !frame.RootElement.GetProperty("success").GetBoolean()) return 8;

// The WPF executable's pre-startup decision.
if (ReportCommandApp.Classify([]) != HostedCommandLineDecisionKind.UserInterface) return 9;
if (ReportCommandApp.Classify(["items", "list"]) != HostedCommandLineDecisionKind.Invocation) return 10;
if (ReportCommandApp.Classify(["--help"]) != HostedCommandLineDecisionKind.Help) return 11;
if (ReportCommandApp.Classify(["nonsense"]) != HostedCommandLineDecisionKind.Invalid) return 12;

Console.WriteLine("Twelve WPF-companion CLI checks passed.");
return 0;
