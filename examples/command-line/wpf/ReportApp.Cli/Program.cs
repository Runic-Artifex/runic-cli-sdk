using Runic.CommandLine;
using Runic.CommandLine.Examples.ReportApp;

// Same composition helper as the WPF App: the two processes share code and configuration, not instances.
return await ReportCommandApp.Create(ReportComposition.CreateReportService(), new SystemCommandConsole()).RunAsync(args);
