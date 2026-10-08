# Adding a CLI to a WPF application

A WPF application on .NET 10 or later can gain a command line without adopting
anything else from Runic and without touching its windows or view models. Only
`Runic.CommandLine` (plus its source generator) is needed, and only in the command
layer. The maintained, compiled example is
[`examples/command-line/wpf`](../../../examples/command-line/wpf).

## The shape

```text
ReportApp.Core      existing application services (IReportService); no Runic, no UI
ReportApp.Commands  [Command] handlers + CommandApp factory + launch classifier (Runic.CommandLine)
ReportApp.Cli       console executable "reportcli": a one-line Main
ReportApp.Wpf       the WPF app (net10.0-windows); its ViewModel calls the same service
Tests               in-memory CLI tests (net10.0, run on every platform)
```

The rule that keeps the UI untouched: **services are the seam**. Move use-case
logic out of code-behind and view models into services (this is good WPF practice
anyway), then let both the view model and the command handler call them. The
view model in the example, `MainViewModel`, has no reference to Runic at all.

## 1. Write handlers over the existing service

```csharp
[Command("items list", Description = "List inventory items.")]
internal static async Task<string> ListItems([FromServices] IReportService reports,
    CancellationToken cancellationToken,
    [Option("--max-quantity", Minimum = 0)] int? maxQuantity = null)
{
    var items = await reports.ListItemsAsync(maxQuantity, cancellationToken);
    return string.Join('\n', items.Select(item => $"{item.Name}: {item.Quantity}"));
}
```

`[FromServices]` resolves through the `ICommandExecutionScopeFactory` the
application supplies. The example scope returns the application's existing service
instance and owns nothing, so disposing the invocation scope never disposes
application services. A real application would adapt its existing container
(for example a `Microsoft.Extensions.DependencyInjection` provider) behind the same
two small interfaces. Handlers stay thin: parse, call the service, return a value.
The framework renders human text or, with `--output=json`, a protocol envelope.

## 2. Add the console executable (recommended)

```csharp
return await ReportCommandApp.Create(new ReportService(), new SystemCommandConsole()).RunAsync(args);
```

Share the application's composition root between `ReportApp.Wpf` and `ReportApp.Cli`
so both build the same services. Ship `reportcli.exe` next to `ReportApp.exe`.

```sh
reportcli items list --max-quantity 10
reportcli report export report.csv --overwrite
reportcli items list --output=json
```

### Why a separate executable

Windows executables carry a subsystem flag. A WPF app is `WinExe` (GUI subsystem):

- It starts with **no console**. `Console.Out` writes nowhere, so the CLI output
  is lost unless a console is attached.
- A shell does **not wait** for a GUI-subsystem process. `cmd.exe` returns to its
  prompt immediately, so scripts cannot read the exit code or capture output with
  `|` or `>` reliably, and `%ERRORLEVEL%` is meaningless without `start /wait`.
- Calling `AttachConsole(ATTACH_PARENT_PROCESS)` can print into the parent console,
  but the prompt has already come back, so output interleaves with the next prompt,
  redirection to pipes behaves inconsistently between shells, and it does nothing
  when launched from Explorer or a service. `AllocConsole` opens a new window,
  which is wrong for scripts.
- A `Exe` (console subsystem) WPF app avoids those problems but flashes a console
  window every time the user double-clicks the application.

A sibling console executable is a normal console program: synchronous, redirectable,
with a real exit code, and it never creates a window. It is the recommended default.
Reserve `AttachConsole` for a single-file requirement you cannot relax, and
test it from `cmd.exe`, PowerShell, Windows Terminal and with redirection.

## 3. Optional: classify before WPF starts

If users may pass arguments to the GUI executable (shell verbs, file associations,
or a mistaken command), decide the launch kind in `Main`, before an `Application`
exists. `ReportCommandApp.Classify` uses `CommandLineHostingAdapter.Classify`
with `EmptyInputFallback.UserInterface`:

```csharp
[STAThread]
public static int Main(string[] args)
{
    if (ReportCommandApp.Classify(args) != HostedCommandLineDecisionKind.UserInterface)
    {
        MessageBox.Show("Run commands with reportcli.exe, for example: reportcli items list");
        return 64;
    }
    var app = new App();
    return app.Run();
}
```

No arguments selects the UI. Anything else is a command, help request, version
request or an invalid launch, and the GUI executable points the user to the
console executable instead of silently opening a window or losing output. The
example uses a code-only `App` class so `Main` can be the entry point
(`StartupObject`); with an `App.xaml`, mark it as a `Page`, not an
`ApplicationDefinition`, and call `InitializeComponent()` yourself.
This is the same pattern as [`HostedExample`](../../../examples/command-line/HostedExample.cs).
The adapter never starts a host, installs signal handlers or disposes your services.

## 4. Test the CLI paths without WPF

Handlers, the classifier and exit codes need no window, so they are tested on any OS
with `CommandAppTester` (see [`wpf/Tests`](../../../examples/command-line/wpf/Tests/Program.cs)):

```sh
dotnet run --project examples/command-line/wpf/Tests
```

## Build matrix

`ReportApp.Wpf` targets `net10.0-windows` and is built by the Windows CI job. The
Linux solution build and `eng/test.sh` cover `ReportApp.Core`, `ReportApp.Commands`,
`ReportApp.Cli` and the tests; the WPF project is deliberately not in
`Runic.CommandLine.slnx`. Build it on Windows with:

```sh
dotnet build examples/command-line/wpf/ReportApp.Wpf -c Release
```

A shared library such as `ReportApp.Core` should target `net10.0` (not a Windows
TFM) so the console executable and tests can reference it on every platform.
