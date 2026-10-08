using Runic.CommandLine;
using Runic.CommandLine.Examples.ReportApp;

// The same composition the WPF App uses: here ReportService is built directly; a real
// application would share its composition root (for example a ServiceProvider factory).
return await ReportCommandApp.Create(new ReportService(), new SystemCommandConsole()).RunAsync(args);
