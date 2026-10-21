# Runic.CommandLine.Testing

Use this package in tests for deterministic command invocation fixtures. It
provides an in-memory console, a simple scope factory, parsed invocations,
captured parse settings, an already-cancelled token, and JSON envelope parsing without reading or
mutating process-global state.

## Test the whole application

```csharp
var tester = new CommandAppTester(console => new CommandApp(catalog)
{
    Console = console,
    ParseSettings = ParseSettings.Default,
    HandleTerminationSignals = false,
});
var result = await tester.RunAsync(["greet", "Ada", "--format=json"]);
using var frame = CommandTestEnvelope.Parse(result.StandardOutput);
```

For prompt tests, construct `TestCommandConsole` with explicit capability flags
and call `QueueInput`. Each tester run gets fresh output buffers.

## Capture an environment

`CommandTestEnvironment.Capture` builds the `ParseSettings` that `CommandApp`
captures from the process environment, from a dictionary instead: its
`RUNIC_COMMANDLINE_OUTPUT` entry selects the output mode, and
`RUNIC_COMMANDLINE_NO_INPUT` and option environment fallbacks read the same
entries. The process environment is never read, and later changes to the
dictionary do not affect the settings.

```csharp
ParseSettings settings = CommandTestEnvironment.Capture(new Dictionary<string, string?>
{
    ["RUNIC_COMMANDLINE_OUTPUT"] = "json",
    ["RUNIC_COMMANDLINE_NO_INPUT"] = "1",
});
var tester = new CommandAppTester(console => new CommandApp(catalog)
{
    Console = console, ParseSettings = settings, HandleTerminationSignals = false,
});
```

`Capture()` without variables is an empty environment.

## Test a continuation

`Run` is the synchronous counterpart of `RunAsync`. It runs the application with
`CommandApp.Run`, so a command that hands a continuation back with
`CommandExecutionContext.ContinueOnCallingThread` has it run on the thread that
calls `Run`:

```csharp
CommandAppTestResult result = tester.Run(["ui", "--no-open"]);
```

`ExitCode` is the continuation's result, or the invocation's exit code when it
failed or handed nothing back. `StandardOutput` and `StandardError` are captured
after the continuation returned. Call it from a test that does not hold a
`SynchronizationContext` the handler depends on; the invocation itself runs
without one.

## Script prompt answers

`CommandPrompts` reads one line per prompt from an interactive
`TestCommandConsole`. Supply the console through `CreateConsole`:

```csharp
var tester = new CommandAppTester(console => new CommandApp(catalog) { Console = console, ParseSettings = ParseSettings.Default })
{
    CreateConsole = () =>
    {
        var console = new TestCommandConsole { IsInteractive = true };
        console.QueueInput("2");      // the second choice of a select, or its label
        console.QueueInput("y");      // a confirm
        return console;
    },
};
```

A queued `null` is the end of input, which cancels the command (exit code 130).
The default noninteractive console never asks: prompts return their defaults or
fail with `RCLI4001`. To drive the redrawn terminal prompts, set
`SupportsAnsi = true` (with `SupportsUnicode` and `Width` as needed) and queue
keys with `QueueKey(ConsoleKey.DownArrow)`, `QueueKey(ConsoleKey.Enter)` or
`QueueText("name\n")`. Their frames, ANSI sequences included, are captured in
`StandardError`. `ReadKeyAsync` waits until a key is queued or the token is
cancelled, which lets a test cancel a waiting prompt. Keep a reference to the
console you create and check `PendingInputCount` and `PendingKeyCount` to make
sure every queued answer was used.
