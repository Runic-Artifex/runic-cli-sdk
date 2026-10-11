# Adding a CLI to a WPF application

A WPF application on .NET 10 or later can gain a command line without adopting
anything else from Runic and without touching its windows or view models. Only
`Runic.CommandLine` (plus its source generator) is needed, and only in the command
layer. The maintained, compiled example is
[`examples/command-line/wpf`](../../../examples/command-line/wpf). It is not an
MVVM template; the view model is just enough to show a service call from the UI.

## The shape

```text
ReportApp.Core      existing application services (IReportService) and ReportComposition; no Runic, no UI
ReportApp.Commands  [Command] handlers + CommandApp factory + launch classifier (Runic.CommandLine)
ReportApp.Cli       console executable "reportcli": a one-line Main (net10.0, never loads WPF)
ReportApp.Wpf       the WPF app (net10.0-windows); its ViewModel calls the same service
Tests               in-memory CLI tests (net10.0, run on every platform)
```

The rule that keeps the UI untouched: **services are the seam**. Move use-case
logic out of code-behind and view models into services (this is good WPF practice
anyway), then let both the view model and the command handler call them. The
view model in the example, `MainViewModel`, has no reference to Runic at all.

### What is shared, and what is not

`ReportComposition.CreateReportService()` is the one place that builds the
services; the WPF `App` and `reportcli` both call it. The two executables are two
processes. They share **code and configuration, not instances**. State held in memory
by the running GUI (an open document, an unsaved edit, a cache) is invisible to
the CLI. If a command must observe or change a running GUI, coordinate through a
file, a database or inter-process communication (a named pipe, for example); the
services should be written so that durable state lives there.

## 1. Write handlers over the existing service

```csharp
[Command("items list", Description = "List inventory items.")]
internal static async Task<string> ListItems([FromServices] IReportService reports,
    [Option("--max-quantity", Minimum = 0)] int? maxQuantity = null,
    CancellationToken cancellationToken = default)
{
    var items = await reports.ListItemsAsync(maxQuantity, cancellationToken);
    return string.Join('\n', items.Select(item => $"{item.Name}: {item.Quantity}"));
}
```

`[FromServices]` resolves through the scope factory the application supplies.
The example passes its service instance with
`CommandScopes.FromServices(CommandServices.Empty.With(reports))`; such a scope
owns nothing, so ending an invocation never disposes application services. An
application with a container passes its provider to `CommandScopes.FromServices`,
or `CommandScopes.Create(provider.CreateAsyncScope, scope => scope.ServiceProvider)`
for one `Microsoft.Extensions.DependencyInjection` scope per invocation. Handlers
stay thin: parse, call the service, return a value. The
framework renders human text or, with `--output=json`, a protocol envelope.

The generator injects `CancellationToken` by type. Declare it last with
`= default` so it can follow defaulted options, as CA1068 recommends.

## 2. Add the console executable (recommended)

```csharp
return await ReportCommandApp.Create(ReportComposition.CreateReportService(), new SystemCommandConsole()).RunAsync(args);
```

```console
reportcli items list --max-quantity 10
reportcli report export report.csv --overwrite
reportcli items list --output=json
```

`ReportApp.Cli` targets plain `net10.0` and does not reference the WPF project or
`PresentationFramework`, so it starts fast and runs where WPF cannot (servers, Linux
CI). Put both executables in one folder. Framework-dependent publish to the same
output directory shares the common assemblies:

```powershell
dotnet publish examples/command-line/wpf/ReportApp.Wpf -c Release -o artifacts/app
dotnet publish examples/command-line/wpf/ReportApp.Cli -c Release -o artifacts/app
```

### Why a separate executable

Windows executables carry a subsystem flag. A WPF app is `WinExe` (GUI subsystem):

- It starts with **no console**, so `Console.Out` writes nowhere unless a console is attached.
- `cmd.exe` and PowerShell do **not wait** for a GUI-subsystem executable typed at the
  prompt: they return immediately, so `%ERRORLEVEL%` / `$LASTEXITCODE` do not reflect
  its result. Piping or redirecting (`app.exe | more`) makes the shell wait, which hides
  the problem in some tests and not in others. `start /wait` or `Start-Process -Wait`
  is required to wait deliberately.
- `AttachConsole(ATTACH_PARENT_PROCESS)` can write into the parent console, but the
  prompt has usually already returned, so the output interleaves with the next prompt.
  Stdout redirection is not honored the way it is for a console program, and nothing
  attaches when launched from Explorer or a service. `AllocConsole` opens a new window,
  which is wrong for scripts.
- An `Exe` (console subsystem) WPF app waits properly but flashes a console window
  every time a user double-clicks it.

A sibling console executable is an ordinary console program: synchronous,
redirectable, with a real exit code, and it never creates a window. It is the
recommended default.

An alternative is the `devenv.com`/`devenv.exe` pairing used by Visual Studio: ship a
small console-subsystem `ReportApp.com` beside `ReportApp.exe`. When a user types
`ReportApp`, the command processor finds `.com` before `.exe` and runs the console one,
which can run commands directly or start the GUI. Explorer and shortcuts still
launch the `.exe`. It keeps one name for users at the cost of an extra launcher and
`PATHEXT` behavior to test.

## 3. Optional: classify before WPF starts

Users can still launch the GUI executable with arguments: a file association, "Open
with", drag-and-drop or a typed command. Decide the launch kind in `Main`, before an
`Application` exists. `ReportCommandApp.Classify` uses
`CommandLineHostingAdapter.Classify` with `EmptyInputFallback.UserInterface` and
routes on the decision's `IsCommandLineRequest`:

- A known command, help, version or completion request goes down the CLI path.
- Everything else, including no arguments and a document path such as `C:\x.rpt`,
  starts the UI. The application reads its own arguments from `StartupEventArgs.Args`
  in `OnStartup` (the example opens the first argument into the window title).

```csharp
[STAThread]
public static int Main(string[] args)
{
    if (ReportCommandApp.Classify(args) == ReportLaunchMode.Command)
    {
        // Best effort, never modal in a script: use the parent console if there is one.
        if (AttachConsole(AttachParentProcess)) Console.Error.WriteLine(hint);
        else if (Environment.UserInteractive) MessageBox.Show(hint);
        return 64;
    }
    return new App().Run();
}
```

The trade-off is that a **mistyped command name opens the UI** instead of reporting
an error, because it is indistinguishable from a file name. `IsCommandLineRequest`
narrows this by treating a known command with bad arguments (`report export`
without a path) as a command: `MatchedPath` names the catalog command or group the
leading arguments matched, so no hand-maintained list of command names is needed. A GUI-subsystem
executable is not the place to run the command,
so the CLI path only prints a hint and exits with 64 (`EX_USAGE` from
`sysexits.h`: the command was used on the wrong executable). Scripts can test for it.
The dialog appears only when the session is interactive and there is no parent
console, so `ReportApp.exe --help` in CI or a scheduled task never blocks.

The example uses a code-only `App` class so `Main` can be the entry point
(`StartupObject`). To keep an `App.xaml`, make the generated entry point go away and
compile the file as a page, then call `InitializeComponent()` yourself:

```xml
<PropertyGroup>
  <EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>
  <StartupObject>MyApp.Program</StartupObject>
</PropertyGroup>
<ItemGroup>
  <Page Include="App.xaml" />
</ItemGroup>
```

```csharp
var app = new App();
app.InitializeComponent();
return app.Run();
```

This is the same pattern as [`HostedExample`](../../../examples/command-line/HostedExample.cs).
The adapter never starts a host, installs signal handlers or disposes your services.

## 4. Test the CLI paths without WPF

Handlers, the classifier and exit codes need no window, so they are tested on any OS
with `CommandAppTester` (see [`wpf/Tests`](../../../examples/command-line/wpf/Tests/Program.cs)):

```sh
dotnet run --project examples/command-line/wpf/Tests
```

## Build matrix

`ReportApp.Wpf` targets `net10.0-windows` and is built by the Windows CI job, which
also runs the built `reportcli.exe` (output redirected to a file, exit code and
content asserted) and launches `ReportApp.exe items list` to assert it exits with 64
without opening a window. The Linux solution build and `eng/test.sh` cover
`ReportApp.Core`, `ReportApp.Commands`, `ReportApp.Cli` and the tests; the WPF project
is deliberately not in `Runic.CommandLine.slnx`. Build it on Windows with:

```powershell
dotnet build examples/command-line/wpf/ReportApp.Wpf -c Release
```

A shared library such as `ReportApp.Core` should target `net10.0` (not a Windows
TFM) so the console executable and tests can reference it on every platform.
