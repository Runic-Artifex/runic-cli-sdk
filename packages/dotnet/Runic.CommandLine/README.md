# Runic.CommandLine

`Runic.CommandLine` is the command kernel for .NET 10 applications that need
portable command behavior and predictable output. Define an immutable catalog,
bind parsed values into typed options, execute a closed handler, and emit human
text or a machine-readable response without coupling the public model to a
parser, Generic Host, or UI framework.

## Install

```bash
dotnet add package Runic.CommandLine
```

The package targets `net10.0` and includes its portable contracts and host
launch classification. Until 1.0 its version is `0.y.z`: a new minor (`0.9.0`)
may contain breaking changes, a patch (`0.8.1`) only fixes or adds, and only
the latest minor gets fixes. Read the release notes before adopting a new minor
in a production command contract.

The embedded source generator needs the compiler of a .NET 10 SDK (any feature
band from 10.0.100): `dotnet build`, Visual Studio with the SDK's compiler,
Rider or the C# Dev Kit. A build whose compiler runs on .NET Framework, such as
Visual Studio's own MSBuild compiler, cannot load the generator: it reports
`CS8032`, and `Runic.CommandLine.Generated` is missing. Hand-built catalogs (see
[lower-level registration](#lower-level-registration)) do not need the generator.

## A complete command-line application

The package includes its incremental generator. Define a static method and run the
catalog; no custom options binder, result DTO, JSON context, service provider or
Generic Host is needed for this example:

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Generated;

return await new CommandApp(GeneratedCommandCatalog.Create())
{
    Name = "hello",
    Version = "1.0.0",
}.RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Say hello.", Examples = ["hello greet Ada"])]
    [DefaultCommand]
    internal static string Greet([Argument] string name = "world") => $"Hello, {name}!";
}
```

`GeneratedCommandCatalog` is `internal` and marked `[GeneratedCode]`: each
assembly with `[Command]` methods gets its own. To build the catalog from another
assembly, such as a test project or the executable of a library of commands,
expose a public method in the assembly that declares the commands, for example
`public static CommandCatalog Create() => GeneratedCommandCatalog.Create();`. The
generated code calls `GeneratedCommandBinding` in `Runic.CommandLine.Generated`;
that type is a support API for the generator of the same major version, hidden
from IntelliSense, and not for hand-written calls.

`hello`, `hello Ada`, and `hello greet Ada` execute the default command.
`hello help greet` and `hello greet --help` show catalog help. `hello --format json
greet Ada` emits one versioned response. An application without a default command
shows help for an empty invocation; the lower-level parser retains its existing
empty-input classification for hosted UI launch decisions.

An application whose grammar is one command marks it `[RootCommand]` instead.
Its name is then not a command spelling, so `hello run` passes `run` to it as an
argument, and `hello --help` is that command's help: its description,
arguments, options and examples, followed by the application's other commands.
The name stays the command's identity in JSON output (`"command": "greet"`).
Hand-built catalogs call `builder.RootCommand("greet")`.

See the runnable [command-line example](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/examples/command-line/README.md).

### Choose the output selector

`--format human|json` selects human or JSON output, before or after the command
and never after `--`; `RUNIC_COMMANDLINE_OUTPUT` applies when it is absent. The
catalog owns the spelling, because it is part of the application's grammar, so
help, completion, the hosting adapter and every runner use the same one. Declare
another spelling, and optionally hidden aliases that keep former spellings
working, on the catalog builder:

```csharp
// Keep the spelling of 0.7 and earlier.
CommandCatalog legacy = GeneratedCommandCatalog.Create(builder => builder.OutputSelector("--output"));
// Adopt --format and keep accepting --output without showing it.
CommandCatalog adopted = GeneratedCommandCatalog.Create(builder => builder.OutputSelector("--format", "--output"));
```

A hidden alias parses exactly like the selector (`--output json`,
`--output=json`, before the command), but help, completions and "did you mean"
suggestions show only the selector, and diagnostics about the value name the
selector. Giving the selector and an alias is a usage error (`RCLI1016`), as is
giving one spelling twice (`RCLI1007`). A command option spelled like the selector
or an alias fails catalog validation with `RCLI0026`, including an option named
`--format` when the catalog declares no selector: rename the option, or declare
another selector. `CommandCatalog.Combine` requires the combined catalogs to
declare the same selector; a catalog that declares none takes the declared one.

### Keep an existing `--json` flag

An application that already has a `--json` flag can keep its users' command
lines by declaring the flag as an alias of the output selector:

```csharp
CommandCatalog catalog = GeneratedCommandCatalog.Create(builder => builder.JsonOutputAlias("--json"));
```

`hello greet Ada --json` is then `hello greet Ada --format json`. The flag takes
no value, is recognized wherever the selector is (also before the command, never
after `--`), stands for the catalog's selector whatever its spelling, and is
listed in help and completions. Giving both `--json` and `--format` is a usage
error (`RCLI1016`), and a command option spelled `--json`, or an alias spelled
like the selector or one of its hidden aliases, fails catalog validation
(`RCLI0024`).

When the existing flag's consumers read the bare document rather than an
envelope, declare it with the payload shape:

```csharp
CommandCatalog catalog = GeneratedCommandCatalog.Create(
    builder => builder.JsonOutputAlias("--json", JsonOutputShape.Payload));
```

`hello greet Ada --json` then writes `"Hello, Ada!"` and one LF to standard
output, the payload serialized with the command's result metadata and nothing
else. A command without a result writes nothing. A failed or cancelled command
writes the payload of its declared failure data (see "Declared domain failure
and recovery data") the same way, or nothing when it declared none. Its
diagnostics are human text on standard error, followed by its `humanOutput`, written
exactly in place of the `CODE: message` fault line, so a legacy contract keeps its
own error text; without `humanOutput` the fault line is written. The exit code is the one the
envelope would report. Help, version and usage errors on a command line that
gives the flag are human text, so `hello --bogus --json` writes its `RCLI1001`
line to standard error. `--format json` and `RUNIC_COMMANDLINE_OUTPUT=json` keep
writing the `runic.commandline/1` envelope, and `CommandExecutionContext.OutputShape`
tells a handler or a custom outcome sink which shape was selected. Failure data
keeps its 65,536-byte bound, because the same outcome can also be written as an
envelope; a success payload is not bound by the protocol's frame limit.

### Read arguments from response files

An application whose command lines grow long can read arguments from response
files, as the .NET CLI does. It is off unless the catalog declares it, because
an argument may legitimately start with `@`:

```csharp
CommandCatalog catalog = GeneratedCommandCatalog.Create(builder => builder.ResponseFiles());
```

`hello @greet.rsp` then runs as if the arguments in `greet.rsp` had been given
in its place, before parsing, so a file may hold the command, options, values
and the output selector:

```text
# Comment lines start with #.
greet
"Ada Lovelace"
--format json
```

A response file is UTF-8, with or without a byte order mark. Arguments are
separated by white space, including line breaks, and quoted as on the Windows
command line: `"two words"` is one argument, a backslash is literal unless it
precedes a double quote (`C:\work\` and `\\server\share` stay as written), and
`\"` is a literal quote. A line whose first non-blank character is `#` is a
comment. `@other.rsp` inside a response file is relative to that file's
directory, and files nest at most 16 levels deep; `@file` on the command line
is relative to the current directory. `@@scope` is the literal argument
`@scope`, and `@` alone is an ordinary argument. In PowerShell, quote the
argument (`hello '@greet.rsp'`), because a bare `@name` is splatting there.

A file that cannot be read (`RCLI1017`), is not valid UTF-8 (`RCLI1018`), has
an unterminated quote (`RCLI1019`), is nested too deeply (`RCLI1020`) or names
itself (`RCLI1021`) is a usage error, exit code 2, in the selected output
format. Help lists `@<file>` with the options, and the generated completions
complete a file name after `@`. `CommandLineHostingAdapter.Classify` expands
the files too; `ICommandSyntaxAdapter.Parse` and `CommandTestInvocation.Parse`
take arguments as given. Without the declaration, `@greet.rsp` is an ordinary
argument.

## Method-first inputs and results

- Scalars: strings, integers, long integers, decimals, doubles, GUIDs, enums and
  nullable scalars. Boolean options are presence flags; nullable Boolean options
  accept an explicit Boolean value.
- C# defaults and nullable inputs are optional. A non-nullable scalar option with
  no default is required. `Required = true` explicitly requires flags and lists,
  and on an `[Argument]` requires at least one value of a list argument or a
  value of a nullable one.
- Arrays and `IReadOnlyList<T>` support the scalar types above. Options append one
  value per occurrence; `AllowMultipleValues = true` also accepts several values
  after one occurrence. A variadic positional must be last.
- `[TrailingArguments] IReadOnlyList<string> native` receives every token after
  `--`, for arguments a command forwards to another tool. Positional arguments
  then receive only the tokens before `--`, so `tool build home -- --option x`
  binds `home` to the command's words and `--option x` to `native`, and the
  forwarded tokens never fail the words' choices. Help shows `[-- native...]`,
  and completion after `--` offers the trailing arguments' choices. Hand-built
  catalogs call `TrailingArguments(id, name)` on the command builder.
- `[Option(..., Description = "...", ValueName = "PATH", Choices = ["a", "b"],
  EnvironmentVariable = "APP_VALUE", Sensitive = true)]` supplies shared metadata.
  Explicit input wins over the environment, then C# defaults. A flag's
  environment value may be `true`/`false`, `yes`/`no` or `1`/`0` in any case.
  Choices match case-insensitively and bind the declared spelling. Sensitive
  defaults and choices are excluded from help and completion.
- Generated numeric metadata permits separated negative numbers. Other
  option-looking data still requires `--` or an equals value. Unknown options and
  duplicate scalar options remain errors.
- `[Command("config show")]` creates a help-only `config` group automatically.
  Describe it with `[CommandGroup("config", Description = "Show and change
  settings.")]` on any class that declares commands (also `DescriptionKey`,
  `LongDescription` and `Hidden`), or with `builder.Group("config", new
  CommandHelp("..."))`. `GeneratedCommandCatalog.Create(builder => builder.GlobalOption("verbose",
  "--verbose", CommandArity.Zero))` adds an option to all commands; it may precede
  the command. Read shared bindings from `ParsedInvocation.Options` in a binder.
- `[ConvertWith(typeof(Converter))]` selects an `ICommandValueConverter<T>` with
  static `Parse(string)`. `[ValidateWith(typeof(Validator))]` selects an
  `ICommandValueValidator<T>` with static `IsValid(T)`. Converted values are stored
  once during binding and reused by the handler.
- `string`, `void`, `Task` and `ValueTask` results have built-in codecs, as does
  `CommandUnit`, also as `Task<CommandUnit>`, `ValueTask<CommandUnit>` or
  `CommandOutcome<CommandUnit>`. A `CommandUnit` command behaves like a `void`
  one: payload type `runic.unit/1`, no human output, and nothing on standard
  output for a payload-shaped JSON alias. Typed
  application payloads retain `[CommandResult("example.result/1", typeof(JsonContext))]`
  and source-generated JSON metadata. An `int` result is payload data, never an
  implicit exit code. Use `CommandOutcome<T>` and an exit policy for domain exits.
- Declare the result's JSON context with camelCase property names, the
  convention of the `runic.commandline/1` envelope and of Runic payloads.
  System.Text.Json otherwise keeps the C# names (`{"Status":…}`), and the
  generator warns with `RCLI9044`:

  ```csharp
  [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
  [JsonSerializable(typeof(Report))]
  internal sealed partial class ReportJsonContext : JsonSerializerContext;
  ```

  The default human output prints the same names, so its rows read
  `status: Ready`; register a presenter with `builder.Present` for other labels.

CancellationToken, CommandExecutionContext and ICommandConsole parameters are
injected automatically; other services use `[FromServices]`. Declare the token
last as `CancellationToken cancellationToken = default` so it can follow
defaulted options, as CA1068 recommends. Instance methods and runtime assembly
discovery are intentionally outside the generated model.

Human output of a typed result lists its JSON properties as `name: value` rows.
Booleans print as `true`/`false`, lists of scalars are joined with commas, and
nested objects and lists of objects are indented below their label with one
`- ` entry per item. Register a presenter with `builder.Present` (see
[custom results](#discovery-validation-and-custom-results)) for a bespoke layout.

## Services and scopes

Handlers receive `[FromServices]` parameters from the invocation scope that
`CommandApp.ScopeFactory` creates. `CommandScopes` covers the usual cases
without a dependency-injection package:

```csharp
// Application-owned instances; an invocation resolves them and disposes nothing.
ScopeFactory = CommandScopes.FromServices(CommandServices.Empty.With<IReportService>(reports)),

// Any IServiceProvider the application already has, shared the same way.
ScopeFactory = CommandScopes.FromServices(serviceProvider),

// Microsoft.Extensions.DependencyInjection: one service scope per invocation, disposed afterwards.
ScopeFactory = CommandScopes.Create(serviceProvider.CreateAsyncScope, scope => scope.ServiceProvider),
```

`CommandScopes.Create` accepts any container scope type and disposes it
asynchronously when it implements `IAsyncDisposable`, otherwise through
`IDisposable`. A missing service fails the invocation with the sanitized
`RCLI5000` fault. Implement `ICommandExecutionScopeFactory` directly only for
invocation-specific services, or use `CommandApp.CreateScopeFactory`, which
receives the `ParsedInvocation` and returns the scope factory for that
invocation. When both are set, `CreateScopeFactory` wins and `ScopeFactory` is
not used; with neither, handlers get no services.

## Presentation and testing

`CommandApp` owns parsing, output environment selection, help, version, process
signals, execution and exit mapping. Configure `ScopeFactory`, `ExitCodePolicy`,
`OutcomeSink`, or `PresentFrameworkRequest` to preserve existing application
contracts during migration.

Ctrl+C (SIGINT), SIGTERM and SIGQUIT (Ctrl+Break on Windows) cancel the
invocation's token, so a handler that honors it stops gracefully and returns the
`Cancelled` exit code, 130, as a shell reports an interrupted command; service
managers that send SIGTERM get the same graceful stop. A repeated signal while the
invocation still runs exits the process at once with 128 plus the signal number
(130 for Ctrl+C, 143 for SIGTERM, 131 for SIGQUIT). Set
`HandleTerminationSignals = false` when an embedding host already owns process
signals; the runtime's default signal handling then applies.

Cleanup that must finish, such as stopping a child process tree or removing what
the command created, holds a shutdown protection scope. While one is alive, a
repeated signal does not exit the process; the invocation stays cancelled, and
after the last scope is disposed the next signal exits again:

```csharp
catch (OperationCanceledException)
{
    using (context.ProtectShutdown())
    {
        await RemoveCreatedResourcesAsync(CancellationToken.None);
    }

    throw;
}
```

`CommandExecutionContext.ProtectShutdown()` has no effect when `CommandApp` does
not handle the signals: with `HandleTerminationSignals = false`, under the
hosting adapter or with a directly used `CommandExecutor`.

The exit codes follow the Unix 128-plus-signal convention; Windows uses the same
values. On Windows, Ctrl+C and Ctrl+Break map to SIGINT and SIGQUIT, and SIGTERM
corresponds to the system shutdown event. Windows may end the process as soon as
the shutdown handler returns, so cleanup that must finish before shutdown cannot
rely on the cancelled handler completing.

Help wraps descriptions at word boundaries and continues them under the
description column. Width is measured in terminal cells: East Asian wide and
fullwidth characters take two cells and may break between characters; combining
marks take none. A `LongDescription` line that is indented or contains a run of
three or more spaces, such as a list, aligned columns or a code block, is
printed as written; other text, including every summary and parameter
description, wraps (an explicit newline still starts a new line). On an interactive terminal help uses the terminal width
less one column; redirected, machine and test output use
`CommandHelpFormatter.DefaultWidth` (80 columns). `SpectreHelpPresenter` uses its
`SpectreCommandConsole` width instead: a width passed to its constructor as
given, otherwise the terminal width less one column, or 100 columns when output
is redirected. `CommandHelpFormatter.Format` accepts an
explicit `width`.

Install optional `Runic.CommandLine.Spectre`, then set `Console = new
SpectreCommandConsole()` and `HelpPresenter = new SpectreHelpPresenter()`.
The adapter supports literal text, Spectre renderables, progress, and prompts.
ANSI is disabled for redirected output, `NO_COLOR`, and `TERM=dumb`.
For handler presentation, wrap the **injected** console so the current invocation's
capabilities and stream routing are retained.

Handlers that write directly use the injected `ICommandConsole`.
`WriteOutAsync("text")`, `WriteOutLineAsync("text")`, `WriteErrorAsync` and
`WriteErrorLineAsync` accept strings; lines end with LF on every platform.

JSON output writes letters and symbols of every script as UTF-8 (`"Ungültige
Eingabe"`, not `"Ung\u00FCltige Eingabe"`). HTML-sensitive characters (`< > & ' " +`
and the backtick), control characters, U+2028/U+2029 and bidirectional or
zero-width formatting characters stay escaped, so a frame remains valid JSON
that is safe to embed and cannot reorder text in a viewer.

`completion bash|zsh|fish|powershell` generates context-aware completion scripts
from the catalog. Set `CompletionExecutableName` when the help-facing name contains
spaces (for example, `dotnet runic`). Candidates follow the current command path,
its visible options and the choices or path hints for the current value; the
scripts do not perform service lookups. See
[discovery, validation and custom results](#discovery-validation-and-custom-results)
for the shell details.

An application whose users already run `tool --completions bash` declares that
option instead of the command:

```csharp
CommandCatalog catalog = GeneratedCommandCatalog.Create(builder => builder.CompletionOption("--completions"));
```

The option is recognized as the first argument, as `--completions bash` or
`--completions=bash`, appears in root help and in the generated scripts, and
replaces the built-in `completion` command, so a default command can take
`completion` as an ordinary argument. A missing or unknown shell is a usage
error. A command option spelled like it fails catalog validation with `RCLI0025`.

`Runic.CommandLine.Testing` supplies `CommandAppTester`, a configurable
`TestCommandConsole` with queued input, and `CommandTestEnvelope`.
`CommandTestEnvelope.Read<T>(json, typeInfo)` reads captured output as strictly
as `CommandJsonEnvelopeReader.Read<T>` and also rejects members the protocol
does not define, so a test notices an envelope change; it returns the
`CommandResponse<T>` with the typed payload. `CommandTestEnvelope.Parse` checks
the framing only: one unpadded JSON value ending in exactly one LF. It returns
a `JsonDocument` for ad-hoc inspection and does not validate the envelope's
members, its success and failure rules, limits or payload type. Supply
explicit ParseSettings, for example `CommandTestEnvironment.Capture(variables)`, to
keep environment-dependent tests deterministic.

## Interactive prompts

`CommandPrompts` asks questions in the style of `@clack/prompts`: select,
multi-select, confirm and validated text, plus a spinner, a task log, and intro
and outro lines. Create
it from the handler's `CommandExecutionContext`. Prompts observe the
invocation's cancellation token from the context, and also any token passed to
a call:

```csharp
[Command("init", Description = "Create a project.")]
internal static async Task<string> Init(CommandExecutionContext context,
    [Option("--template")] string? template = null, [Option("--yes", "-y")] bool yes = false,
    CancellationToken cancellationToken = default)
{
    var prompts = new CommandPrompts(context, interactive: !yes);
    await prompts.IntroAsync("create-app", cancellationToken);
    template ??= await prompts.SelectAsync<string>("Template",
        [new("web", "Web app", "Vite and Svelte"), new("cli", "Command line")], defaultIndex: 0, cancellationToken);
    string name = await prompts.TextAsync("Project name", validate: value =>
        value.Length == 0 ? "Enter a name." : value.Contains(' ') ? "Use no spaces." : null, cancellationToken: cancellationToken);
    if (!await prompts.ConfirmAsync($"Create {name}?", defaultValue: true, cancellationToken)) return "Nothing created.";

    await using CommandTaskLog log = await prompts.StartTaskLogAsync("Installing", visibleLines: 6, cancellationToken);
    await log.WriteLineAsync("added 120 packages", cancellationToken);   // for example from a process output handler
    await log.SetStatusAsync("12s elapsed", cancellationToken);
    await log.SucceedAsync("Installed", cancellationToken: cancellationToken);   // clears the log lines
    await prompts.OutroAsync($"cd {name} and run it.", cancellationToken);
    return $"Created {name} from {template}.";
}
```

`StartSpinnerAsync(message)` is a task log without visible lines. A task log
keeps the last `visibleLines` lines under its title while it runs; `SucceedAsync`
replaces the block with one summary line (the log stays with `showLog: true`),
and `FailAsync` keeps up to the last 1000 lines below the summary. Lines are
stripped of control characters and ANSI sequences, and members may be called
from process output callbacks.

`IntroAsync(title)` writes `┌  title` before the first prompt, and
`OutroAsync(message)` writes `│` and `└  message` after the last one, as Clack's
`intro` and `outro` do. Like a task log they take no input: they are styled on a
terminal that can redraw, also when prompts are disabled, and are plain lines
otherwise. Their text is stripped of control characters and ANSI sequences.

To show a child process's live output in a task log, as `nix-manager` does,
pass each line from `Runic.CommandLine.Processes` to the log:

```csharp
sealed class TaskLogLines(CommandTaskLog log) : IProcessOutputHandler
{
    public ValueTask OnLineAsync(ProcessOutputLine line, CancellationToken cancellationToken) =>
        log.WriteLineAsync(line.Text, cancellationToken);
}

await using CommandTaskLog log = await prompts.StartTaskLogAsync("nix build", visibleLines: 6, cancellationToken);
ProcessResult result = await runner.RunAsync(request, new TaskLogLines(log), cancellationToken);
if (result.State == ProcessState.Exited && result.ExitCode == 0) await log.SucceedAsync(cancellationToken: cancellationToken);
else await log.FailAsync($"nix build ended in {result.State}", cancellationToken: cancellationToken);
```

On a redrawing terminal `WriteLineAsync` only queues the line for the next
frame, so it does not slow the child; without one it writes the line to
standard error, and the runner's backpressure applies.

**When prompts ask.** A prompt asks only when `IsInteractive` is true:

| Situation | Prompt with a default | Prompt without a default |
| --- | --- | --- |
| Interactive console | Asks; Enter alone accepts the default | Asks |
| JSON output (`--format json`, `RUNIC_COMMANDLINE_OUTPUT=json` or a declared `--json` alias) | Returns the default | Exit code 2, `RCLI4001` `prompt-json-output` |
| Console not interactive (standard input or standard error redirected) | Returns the default | Exit code 2, `RCLI4001` `prompt-requires-terminal` |
| `RUNIC_COMMANDLINE_NO_INPUT` is `1`, `true` or `yes`, or `interactive: false` | Returns the default | Exit code 2, `RCLI4001` `prompt-disabled` |

Interactivity depends on standard input and standard error, where prompts are
written, not on standard output: `cd "$(tool pick-dir)"` and `tool new | tee
log` still ask, and only the command's result goes through the pipe.
`SystemCommandConsole.IsInteractive` follows this rule. The `CI` variable is not
checked: a CI job that runs with a terminal should set
`RUNIC_COMMANDLINE_NO_INPUT=1`.

Noninteractive runs write nothing for a prompt that returns its default. A
multi-select's default is its `defaultIndexes`, which may be empty unless
`required` is set. A text default is returned without validation. With JSON
output, standard output still holds exactly one frame; the failure is the
frame's `RCLI4001` fault and diagnostic, whose argument is the question.
`CommandApp` reads `RUNIC_COMMANDLINE_NO_INPUT` through its environment reader,
so explicit `ParseSettings` without `GetEnvironmentVariable` ignore it. Add your
own `--yes` or `--no-input` option and pass it as `interactive: false`.
Disabling prompts does not affect spinners, task logs, intros and outros, which
take no input: they still animate or are styled on a terminal that can redraw,
and write plain lines only with JSON output or without such a terminal.

**Cancelling.** Ctrl+C while a prompt waits cancels the command with exit code
130 (`RCLI4000`); so do Escape and the end of input (Ctrl+D) at a line prompt. The
prompt leaves a "Cancelled" summary line and shows the cursor again. The cursor
also comes back when a second Ctrl+C exits the process at once, and with
`HandleTerminationSignals = false`, where SIGINT, SIGTERM and SIGQUIT keep their
default action. The .NET runtime restores the terminal's input mode when the
process exits normally or on such a signal. After SIGKILL or a crash of the
runtime nothing can run: if the cursor stays hidden, `tput cnorm` or `reset`
recovers the terminal.

A line prompt cancelled while it waits leaves its read of standard input
running, because the process console cannot abandon a read. The next line
prompt on the same console takes over that read, so the line the user types
next answers it rather than being lost.

**Rendering and accessibility.** Prompts write to standard error. When the
console is an `ICommandTerminal` whose `SupportsAnsi` is true, they redraw in
place and take arrow keys, Space, Enter and Escape. Lines are measured in
terminal columns (CJK and emoji take two, combining marks none) and cut on a
character boundary to the terminal width, which is read for every frame, so a
resized terminal is followed; a block stays shorter than the terminal height,
and long choice lists scroll. `SupportsUnicode` selects `◆ ● ◻` or ASCII
symbols.
`SystemCommandConsole` supports ANSI when standard input and standard error are
terminals, `TERM` is set and is not `dumb`, `NO_COLOR` is not set and, on
Windows, inside Windows Terminal; `SpectreCommandConsole` applies its own colour,
Unicode and width settings and detects ANSI in other Windows consoles. Otherwise,
including with `NO_COLOR` and `TERM=dumb`, every prompt asks one plain question
that a screen reader reads in order and that is answered with one line:

```text
create-app
Template
  1. Web app - Vite and Svelte
  2. Command line
Enter a number from 1 to 2 [Web app]: 2
Project name: demo
Create demo? [Y/n]: y
Installing
added 120 packages
Installing: 12s elapsed
Installing: Installed
cd demo and run it.
```

A select accepts a number or a label; a multi-select accepts numbers or labels
separated by commas; a confirm accepts the translated Yes or No, their first
letters, or `y` and `n`. An unrecognized answer asks again. Without a terminal,
including with JSON output, a task log writes its title, lines, status changes
and summary as plain lines to standard error, and an intro or outro writes its
text as one plain line there.

**Translation.** Hints, Yes and No, errors and the `RCLI4001` diagnostics
resolve through `CommandApp.TextResolver`; the `prompts.*` keys are listed in
[the text keys](../../../docs/guides/command-line/text-keys.md). Questions and
labels are your text: resolve them with
`new CommandTextContext(context.Culture, context.TextResolver).Resolve(key, fallback)`.

**Testing.** Give `CommandAppTester.CreateConsole` an interactive
`TestCommandConsole` and queue one line per prompt with `QueueInput`; a queued
`null` is the end of input. Set `SupportsAnsi = true` and use `QueueKey` and
`QueueText` to drive the redrawn prompts. `PendingInputCount` and
`PendingKeyCount` show whether every queued answer was used.

```csharp
var tester = new CommandAppTester(console => new CommandApp(catalog) { Console = console, ParseSettings = ParseSettings.Default })
{
    CreateConsole = () =>
    {
        var console = new TestCommandConsole { IsInteractive = true };
        console.QueueInput("2");      // Template: Command line
        console.QueueInput("demo");   // Project name
        console.QueueInput("");       // Create demo? accepts the default
        return console;
    },
};
CommandAppTestResult result = await tester.RunAsync(["init"]);
```

## Exit codes

`CommandApp.RunAsync` returns the process exit code. The default
`IExitCodePolicy` maps each `CommandExitCategory` as follows; constants are on
`CommandExitCodes`.

| Code | Category | Typical cause |
| --- | --- | --- |
| 0 | `Success` | The command succeeded, or help, version or a completion script was written. |
| 1 | `CommandFailure` | The command reported an expected failure. |
| 1 | `Unavailable` | A required resource or service was unavailable, including "not found". |
| 2 | `Usage` | Unknown command or option, missing, malformed or out-of-range input (`RCLI1xxx`, `RCLI2xxx`), or a prompt that cannot ask (`RCLI4001`). |
| 2 | `Validation` | A handler rejected well-formed input, for example a business rule. |
| 3 | `HostFailure` | An unexpected exception or infrastructure failure (`RCLI5000`). |
| 130 | `Cancelled` | Ctrl+C, SIGTERM, SIGQUIT or the caller's token cancelled the invocation, or the user cancelled a prompt. |
| 130, 131, 143 | none | A repeated termination signal while the invocation still runs (128 plus the signal number). |

These are the codes every Runic tool reports: 1 for failures and 2 for usage, as
the dotnet CLI does, and 130 for an interrupted command, as a shell does. The
categories stay distinct in `CommandExitCategory`, the JSON fault and observers.
A handler selects a failure category with `CommandOutcome.Failure<T>(category,
fault)`. A custom `IExitCodePolicy` may give categories distinct codes, for
example the former 3, 4, 5, 10 and 70, but zero always means success.

Set `HelpUri` on a `CommandFault` or `CommandDiagnostic` to link its code to
your documentation, for example `new CommandFault("RAS1001", "The archive could
not be read.") { HelpUri = new Uri("https://docs.example.com/errors#ras1001") }`.
JSON output adds a `helpUri` member and human output writes
`Help for RAS1001: <link>` after the line. The link must be an absolute `https`
address; unlike messages and details, it is not redacted, so never build it
from user input.

## Declared domain failure and recovery data

When a failed or cancelled command must return an exact retained directory or
target identity, declare a bounded domain DTO separately from presentation text:

```csharp
var recovery = CommandFailureData.Create(
    "sample.clone-recovery/1", report, RecoveryJsonContext.Default.CloneRecovery);
return CommandOutcome.Failure<CloneResult>(
    CommandExitCategory.Cancelled,
    new CommandFault("CLONE_CANCELLED", "The clone was cancelled. Inspect the retained directory."),
    humanOutput: "Inspect the retained directory before retrying.\n",
    failureData: recovery);
```

`RecoveryJsonContext` is the application's source-generated `JsonSerializerContext`
with `[JsonSerializable(typeof(CloneRecovery))]`; use its `Default` metadata to
preserve configured property names and other JSON defaults. A generated command
keeps its existing `[CommandResult]` declaration for its success DTO. The failure
factory explicitly declares the separate identity and JSON contract; no extra
generator attribute or reflection is required.

The immutable snapshot preserves declared domain values exactly, including paths
and target names that resemble technical text. Include only fields intended for
the command's consumer. Keep secrets, exception/log text, and unrelated internal
state out of the domain DTO. Ordinary fault messages, details, and diagnostics
continue through their sanitizer. Human output uses the existing fault and
optional `humanOutput`; data is not printed automatically.

JSON uses the optional `fault.data` member:
`{"type":"sample.clone-recovery/1","payload":{...}}`. The protocol remains
`runic.commandline/1`, with failure `payloadType` and `payload` both null and a
nonzero exit. Existing version-1 readers ignore the extension. Ordinary failures
retain their exact wire format. Upgrade producer and consumer independently;
retain any application-specific legacy detail encoding only while old consumers
still require it.

`CommandJsonEnvelopeReader.Read` retains validated data on `response.FailureData`.
Decode only an identity the application understands:

```csharp
if (response.FailureData is { } data &&
    data.TryGet("sample.clone-recovery/1", RecoveryJsonContext.Default.CloneRecovery,
        out CloneRecovery? recovery))
{
    // Interpret the application's recovery contract; retain the failed exit.
}
```

`TryGet` returns false for an unknown identity so callers can present the ordinary
fault. A matching identity with an incompatible shape raises
`CommandProtocolException` with `failure-data-shape-mismatch`. Decoding follows
the supplied context's settings: mark required fields and
configure nullability or unknown-member handling there when the domain contract
requires those checks. A type identity alone does not make missing fields invalid.
Snapshots are limited to 65,536 compact UTF-8 JSON bytes and 24 nested objects/arrays; the complete
response keeps its one-mebibyte/32-level limits. Malformed, duplicate, or
oversized data is refused without truncation. `Create` and the reader raise the
same `CommandProtocolException` kinds: `failure-data-byte-limit`,
`failure-data-depth-limit`, `duplicate-property`, or `invalid-failure-data`
(including serializer and encoding failures). Invalid UTF-16 (an unpaired
surrogate) in any string value or property name, including dictionary keys, raises
`invalid-failure-data` instead of being replaced with U+FFFD. Recovery data and `retryable` never
authorize an automatic retry.

## Discovery, validation and custom results

`Hidden = true` on `[Command]`, `[Option]` or `[Argument]` omits that entry from
help listings and completion. Hidden commands/options still parse when explicitly
specified; this is discoverability metadata, not access control. A command's
`LongDescription` adds extended help below its summary. Close command and option
typos receive suggestions from visible catalog spellings only. Suggestions never
execute corrections and never include option values.

`FileInfo` and `DirectoryInfo` parameters bind directly and infer file/directory
completion metadata. Strings can opt in with `PathKind = CommandPathKind.File`
or `.Directory`. `MustExist = true` checks the requested path kind before the
handler runs. These are input checks, not security guarantees: the handler still
opens the file and handles permissions, replacement and filesystem races.

Use `Minimum`/`Maximum` for inclusive numeric input bounds. `Requires` and
`ConflictsWith` refer to stable option IDs, not spellings: parameter `dryRun`
gets ID `dry-run`. Presence includes captured environment fallback; an environment
flag set to `false` is absent. Dependencies do not make an optional flag implicit.
The builder uses the same `CommandHelp` properties. Path and relationship checks
run during execution before binding. Each generated value is then converted,
checked against `Minimum`/`Maximum`, and only then passed to its `[ValidateWith]`
validator, so `--limit abc` reports a type error (`RCLI2005`, "--limit requires a
whole number.") rather than a range error, and a validator never sees a value
outside the declared range. For a hand-written `ICommandOptionsBinder`, bounds
are checked after the binder succeeds. Hosted classification remains free of
filesystem reads. Invalid input returns a safe `RCLI2002` usage fault that names
the option spelling or `<ARGUMENT>`, without echoing its value, for example
"--limit requires a number between 1 and 50.". Both faults carry a matching
`binding` diagnostic, and a value that came from an environment fallback adds an
`RCLI2006` information diagnostic naming the variable. Invalid choices list the
allowed values unless the parameter is `Sensitive` or the list is long.

The source generator reports the catalog rules it can see in attributes at build
time, for example a required argument after an optional one (`RCLI9035`, the
runtime `RCLI0015`), `Minimum`/`Maximum` on a non-numeric parameter, misnamed
`Requires`/`ConflictsWith` IDs and a nested `[DefaultCommand]`. Rules it cannot
see still fail when the catalog is built; `CommandCatalogValidationException`
lists each issue's code, command path and message in its `Message`.

```csharp
[Command("copy", Description = "Copy a file.")]
internal static Task Copy(
    [Option("--source", MustExist = true)] FileInfo source,
    [Option("--destination", PathKind = CommandPathKind.File)] string destination,
    [Option("--overwrite", ConflictsWith = ["dry-run"])] bool overwrite,
    [Option("--dry-run")] bool dryRun,
    CancellationToken cancellationToken) => /* application operation */ Task.CompletedTask;
```

Customize a generated command's human result without another handler or codec:

```csharp
var catalog = GeneratedCommandCatalog.Create(builder =>
    builder.Present<Report>("report", (report, console, culture, token) =>
        console.WriteOutAsync($"Processed {report.Count} items\n".AsMemory(), token)));
```

The command's declared result payload identity and source-generated JSON metadata
remain unchanged. The delegate only runs in human mode. Register against a
canonical command path; a mismatched result type is a catalog validation error.

To own a command's whole human output, failures included, register an outcome
presenter instead of a custom `ICommandOutcomeSink`. It receives the outcome, the
exit code, the binding and outcome diagnostics, the command and the invocation's
`CommandExecutionContext`, and runs only in human mode, in place of the
application's outcome sink; JSON output is unchanged. `WriteDefaultAsync` writes
what the sink would have written, for example for successes:

```csharp
var catalog = GeneratedCommandCatalog.Create(builder =>
    builder.PresentOutcome<PackResult>("pack", (presentation, token) =>
    {
        ICommandConsole console = presentation.Context.Console;
        if (presentation.Outcome.Succeeded)
            return presentation.WriteDefaultAsync(token);
        CommandFault fault = presentation.Outcome.Fault!;
        string source = fault.Details.GetValueOrDefault("sourceDirectory", "?");
        return console.WriteErrorAsync(
            $"{fault.Code}: The source directory '{source}' does not exist.\n".AsMemory(), token);
    }));
```

A presenter writes the fault as given: the human output's sanitizing of
technical fault messages, its localization and the `Help for CODE` line are part
of the default output. The context passed to an outcome presenter or to an
`ICommandOutcomeSink` carries the handler's state: its text resolver, whether
`RUNIC_COMMANDLINE_NO_INPUT` disabled prompts (`new CommandPrompts(context).IsInteractive`),
its continuation and its shutdown protection. Its scoped services have already
been disposed.

Completion scripts use catalog context to select commands and aliases, visible
options and the choices for the current option or positional argument. Native
Bash, Zsh, fish and PowerShell wrappers support separated and `--option=value`
forms, safely dequote typed prefixes and preserve literal choices containing
spaces. Filesystem completion follows visible, non-sensitive path metadata on
options and positional arguments, including aliases and directory-only hints.
The generated scripts request files through catalog path hints; shells may apply
their own default filesystem fallback. The generated scripts require no Python
runtime. Install scripts explicitly in your shell's
completion setup (Zsh requires `compinit`); Runic never modifies shell profiles.

`CommandCompletion.Query(catalog, words)` provides the same context query for
integrations. Exclude the executable from `words`; the final word is the current
prefix, or an empty string after trailing whitespace.
`CommandCompletion.Generate(catalog, executable, shell)` writes a script directly.
Both complete the catalog's output selector, never its hidden aliases.
See the [complete examples](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/examples/command-line/README.md).

Converters and validators must be closed, accessible class or struct types
implementing `ICommandValueConverter<T>` or `ICommandValueValidator<T>` for the
exact parameter type, with concrete static implementations. Generator diagnostics
point to invalid attributes. Boolean options are presence flags: they support
validators but reject converters. List binding converts supported element types
and rejects aggregate converters; list validators receive the full bound list.

## Explicit culture and localized presentation

Set `CommandApp.Culture` and `CommandApp.TextResolver` to resolve help and
framework/execution diagnostics without selecting a localization library. The
culture defaults to `CultureInfo.CurrentUICulture`, the user's display language,
captured when an invocation starts; framework prose remains English when no resolver is installed. Direct
`CommandHelpFormatter.Format` calls retain an English default and have an
additive overload accepting `CommandTextContext`. Hosted presentation uses the
culture passed to `CommandLineHostingAdapter.PresentAsync` and the resolver on
its `CommandPresentation`. Configure `CommandOutputDispatcher.TextResolver` for
a custom or hosted execution sink.

`ICommandTextResolver.Resolve` receives a key, culture and ordered safe argument
list, and returns null when it cannot resolve the key. Command, argument and
option attributes accept `DescriptionKey`; `Description` remains the literal
fallback. Diagnostic messages resolve their `MessageKey` and `Arguments`.
Faults without a matching diagnostic use `faults.{code}` with no arguments; a
diagnostic whose own key is unresolved also falls back to `faults.{code}`.
`CommandTextKeys` lists every framework key with constants such as
`CommandTextKeys.HelpUsage`, and `CommandTextKeys.All` gives each key's ordered
argument names; the [framework text key table](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/docs/guides/command-line/text-keys.md)
shows them with their English text. Value errors use `diagnostics.{kind}` keys
such as `diagnostics.invalid-integer` or `diagnostics.out-of-range`, whose first
argument is the option spelling or argument placeholder. A typo suggestion is a
trailing argument with its own key, such as `diagnostics.unknown-option-suggestion`.
`CommandTextContext.ValidateDescriptionKeys(catalog)`
reports each `DescriptionKey` the resolver does not resolve (`RCLI0023`), so a
test can catch a typo that would otherwise show the literal fallback; with
`RUNIC_COMMANDLINE_DEBUG=1`, help also prints them to stderr as warnings.
Translations change presentation text, including JSON messages, while command
spellings, canonical paths, diagnostic codes/keys and protocol/payload identities
remain unchanged. The existing sanitizers still apply to resolved text.

With Runic Translations, set `TextResolver = new TranslationCommandTextResolver(snapshot)`
from the `Runic.Translations.CommandLine` package: it looks up each key in your
catalog by name, falls back to its built-in English and German framework text,
and needs no key mapping. The [`Runic.Translations.CommandLine`](https://www.nuget.org/packages/Runic.Translations.CommandLine) package README shows an English/German setup.
Both are optional; this package does not reference Translations.

## Commands inside a Runic application

Use `CommandLineHostingAdapter` when the application already owns startup,
services, cancellation and the choice between CLI and UI. It uses the same
catalog, generated binders, executor and framework presentation as `CommandApp`.
It does not start a Generic Host, subscribe to Ctrl+C, or dispose your application.

Create it from the application's `CommandApp` so name, version, text resolver,
help presenter, scope factory, exit policy and observers are declared once:

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Hosting;

var cli = new CommandLineHostingAdapter(CreateCommandApp(services));
var launch = new HostedCommandLineLaunchInput(args,
    outputEnvironmentValue: Environment.GetEnvironmentVariable("RUNIC_COMMANDLINE_OUTPUT"),
    emptyInputFallback: EmptyInputFallback.UserInterface)
{
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["MY_APP_ENV"] = Environment.GetEnvironmentVariable("MY_APP_ENV"),
    },
};
var decision = cli.Classify(launch);
if (!decision.IsCommandLineRequest)
{
    await cli.WriteSuggestionAsync(decision, cancellationToken: applicationStopping);
    return await OpenApplicationUiAsync(applicationStopping);
}
return await cli.RunAsync(decision, cancellationToken: applicationStopping);
```

`Classify` is synchronous, so a desktop entry point can decide before its UI
loop starts. `IsCommandLineRequest` is true for an invocation, help, version or
completion request, and for an invalid launch whose leading arguments name a
known command or group (`MatchedPath`, `MatchesKnownCommand`). Everything else,
such as a document path or an unknown word, belongs to the UI; no list of
command names needs to be maintained. When an unknown word is a close match for a
visible command, such as `lst` for `list`, `SuggestedCommand` names it and
`WriteSuggestionAsync` writes the same "Did you mean 'list'?" line the standalone
command line shows to standard error, so the typo is not silent; the UI still
opens, because the word may be a document. `RunAsync` presents or executes the
decision on the app's `Console` (or the console you pass) in `CommandApp.Culture`
or `CultureInfo.CurrentUICulture`, and returns the exit code.

The application captures only the environment values its options declare.
`EnvironmentVariables` copies that dictionary; missing keys never fall back to the
process environment. Explicit arguments override captured values, which override
handler defaults. Output-format selection continues to use the separate captured
`outputEnvironmentValue`. Names in the parameter snapshot are matched exactly.

For full control, construct the adapter from a catalog and a `CommandExecutor`,
set `Presentation`, and call `PresentAsync` and `ExecuteAsync` yourself:

```csharp
var cli = new CommandLineHostingAdapter(catalog, new CommandExecutor(applicationCommandScopes))
{
    Presentation = new() { Name = "my-app", Version = version, ExceptionObserver = RecordInternalException },
};
var decision = cli.Classify(launch);
if (!decision.CanExecute)
    return await cli.PresentAsync(decision, console, culture, correlationId, applicationStopping);
return (await cli.ExecuteAsync(new(decision, console, culture, correlationId, new CommandOutputDispatcher())
{
    ExceptionObserver = RecordInternalException,
}, applicationStopping)).ExitCode;
```

`Classify` creates no service scope. `PresentAsync` handles scoped help, version,
usage failures and `completion bash|zsh|fish|powershell`, also without a scope.
Help and errors respect human/JSON output selection; completion intentionally
writes the raw script, the same context-aware script `CommandApp` produces. Existing hosts
may keep their own presenters and use the decision's public diagnostics instead.
`PresentAsync` accepts framework decisions created by that same adapter and
rejects UI and invocation decisions. Hosts use the concrete
`CommandLineHostingAdapter`; decisions come only from its `Classify`.

`ExecuteAsync` owns only the invocation scope returned by your scope factory.
Use `[FromServices]` for handler dependencies. Pass your host's cancellation token
and set `ExceptionObserver` to your internal logging callback; exception details
stay out of public faults. If you customize exit codes, supply the same policy to
`CommandExecutor` and `Presentation.ExitCodePolicy`. An explicit empty-input UI
policy wins even when the catalog declares a default command.

See the runnable [hosted example](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/examples/command-line/HostedExample.cs)
for service injection and the complete launch flow. The example's UI branch is a
console placeholder for an application's existing UI launcher.

### Start a UI host on the main thread

Some hosts must start on the process's main thread before any `await`, such as a
desktop host whose event loop owns that thread. `CommandApp.RunAsync` parses and
executes asynchronously, so a command cannot start such a host itself. Instead it
hands a synchronous continuation back to `Main` with
`CommandExecutionContext.ContinueOnCallingThread`, and `Main` runs the application
with `CommandApp.Run`, which blocks until the invocation completes and then
invokes the continuation on the thread that called it:

```csharp
// Program.cs: a synchronous Main, so this thread is the process's main thread.
return CreateCommandApp().Run(args);

internal static class DashboardCommands
{
    [Command("ui", Description = "Open the dashboard.")]
    internal static async Task Ui(CommandExecutionContext context,
        [Option("--flake")] string? flake = null, CancellationToken cancellationToken = default)
    {
        // Load and validate asynchronously; a failure here is an ordinary command fault.
        DashboardSettings settings = await DashboardSettings.LoadAsync(flake, cancellationToken);
        context.ContinueOnCallingThread(() =>
            DesktopEventLoop.Run(DashboardHost.Options(), desktop => Dashboard.RunAsync(settings, desktop)));
    }
}
```

`Run` returns the continuation's result as the exit code. It runs the continuation
only when the invocation succeeded and was not cancelled, after its output was
written and its scope disposed, so capture values rather than scoped services.
Help, version and usage errors never reach the command. The invocation runs
without the caller's `SynchronizationContext`, and `HandleTerminationSignals` signal
handling ends before the continuation starts; the host handles its own shutdown.
An exception from the continuation propagates from `Run`.

Test such a command with `CommandAppTester.Run`, the synchronous counterpart of
`RunAsync`: it runs the continuation on the calling thread and returns its exit
code with the output captured after it returned.

`CanContinueOnCallingThread` is false under `RunAsync`, the hosting adapter's
`RunAsync` and a directly used `CommandExecutor`, where `ContinueOnCallingThread`
throws `InvalidOperationException` (a `RCLI5000` host failure). A command that can
also run there checks it first.

The hosting adapter does not own the entry point or its thread, so it never runs
a continuation itself. A host that can run one opts in with
`AcceptsContinuation` on `HostedCommandLineExecutionInput` and receives it in
`HostedCommandLineExecutionResult.Continuation`, after the command's output was
written and its scope disposed, and only when the command succeeded and the
host's token was not cancelled. The host runs it on the thread it chooses and
orders it against its own shutdown:

```csharp
static int Main(string[] args)
{
    var adapter = new CommandLineHostingAdapter(app);
    HostedCommandLineDecision decision = adapter.Classify(new HostedCommandLineLaunchInput(args));
    if (!decision.CanExecute) return StartHostOrPresent(decision);
    HostedCommandLineExecutionResult result = adapter.ExecuteAsync(
        new HostedCommandLineExecutionInput(decision, console, culture, correlationId, sink) { AcceptsContinuation = true })
        .AsTask().GetAwaiter().GetResult();
    return result.Continuation is { } next ? next() : result.ExitCode;
}
```

`Main` must not `await` before calling `Run`: after an `await`, a top-level program
resumes on a thread-pool thread. The mechanism has no UI dependency and works
with generated and explicitly registered commands and NativeAOT.

### Framework presentation failures and command ownership

Help, version, usage-error and completion presentation honor cancellation in both
runners. Cancellation returns the configured `Cancelled` exit code. Other nonfatal
presentation failures return the configured `HostFailure` exit code and notify
`CommandApp.ExceptionObserver` or, for hosted presentation,
`CommandPresentation.ExceptionObserver`. Execution continues to use the observer
on `HostedCommandLineExecutionInput`. Observer failures do not replace the original
exit result. Fatal runtime failures propagate.

A presenter or output stream may already have written partial output when it
fails. The framework therefore does not retry output or append a second error
frame; use the exit code and your internal observer to detect these failures.
Argument/decision ownership errors in `PresentAsync` remain API usage exceptions.

A catalog-owned root command or alias named `completion` takes precedence over the
built-in script command, and a declared `CompletionOption` replaces it. This works for manual and generated catalogs. Built-in
scripts complete the catalog's output selector (for example `--runic-output`
after `OutputSelector("--runic-output")`); the hosting adapter and `CommandApp`
read it from the same catalog, so they agree.

When a global option reuses a command-local definition, its name, arity and alias
set must match. Alias ordering is irrelevant; mismatches fail catalog validation
with `RCLI0019`, rather than producing command-dependent parsing behavior. The
issue names the option ID and both definitions' names and alias sets.

## Migrating from 0.2

Existing explicit catalogs, payload identities and exit policies continue to work.
Replace duplicated startup with `CommandApp` progressively. Retain domain outcome
sinks and custom framework presenters when scripts depend on an existing envelope.

Generated nullable inputs now bind absence as null, optional positional defaults
are honored, and required non-nullable scalar options fail during parsing. Binding
errors identify the parameter and expected type without echoing its value. `help
<command-path>`, prefix output selection, and empty default-command invocations
are now accepted. The version-1 machine envelope has not changed.

For applications with their own `--format` option, declare another selector on
the catalog, for example `builder.OutputSelector("--runic-output")`. Set the
`GetEnvironmentVariable` callback of explicit `ParseSettings` if the application
also declares parameter environment fallbacks. `ParseSettings` itself never reads
the process environment.

## Lower-level registration

Register a command with its binder, handler factory, and source-generated result
codec. Parse captured arguments, then pass a successful invocation to the
executor and `CommandOutputDispatcher`.

```csharp
CommandCatalog catalog = new CommandCatalogBuilder()
    .Command<HelloOptions, HelloHandler, Greeting>("hello", command => command
        .Describe("command.hello")
        .BindWith(HelloBinder.Instance)
        .CreateHandlerWith(HelloHandlerFactory.Instance)
        .Produces(GreetingCodec.Instance))
    .Build();

ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
    catalog,
    args,
    new ParseSettings(Environment.GetEnvironmentVariable(
        CommandOutputClassifier.EnvironmentVariableName)));

if (parse.Kind == ParseOutcomeKind.Invocation && parse.Invocation is not null)
{
    var request = new CommandExecutionRequest(
        parse.Invocation, console, CultureInfo.InvariantCulture, "request-42");
    CommandExecutionResult result = await executor.ExecuteAsync(
        request, new CommandOutputDispatcher(), cancellationToken);
    return result.ExitCode;
}
```

`console` is your `ICommandConsole` implementation and `executor` is a
`CommandExecutor` configured with your `ICommandExecutionScopeFactory`. See the
[complete runnable example](https://github.com/Runic-Artifex/runic-cli-sdk/tree/main/tests/native/Runic.CommandLine.AotSmoke)
for implementations of the binder, handler, source-generated codec, scope, and
console.

Set `RUNIC_COMMANDLINE_OUTPUT=json` to write a single UTF-8 JSON response frame
to stdout; the default is human output. The portable adapter also recognizes an
explicit `--format human` or `--format json` value (or the catalog's declared
selector), which takes precedence over the captured environment value.

## When to use it

Choose this package for the command model, host launch classification, and
execution pipeline. Add
[`Runic.CommandLine.Processes`](https://www.nuget.org/packages/Runic.CommandLine.Processes)
when commands start child processes, and
[`Runic.CommandLine.Spectre`](https://www.nuget.org/packages/Runic.CommandLine.Spectre)
for rich terminal output. The portable contracts remain directly available from
this package when your own integration exposes or implements them.

Catalog validation reports invalid names, duplicate spellings, invalid arity,
and incomplete registrations together in deterministic definition order.
Execution creates and disposes exactly one scope for each valid invocation; a
success is the only semantic outcome that maps to exit code zero.

## Documentation and support

Read the [Runic Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line/),
see [examples](https://github.com/Runic-Artifex/runic-cli-sdk/tree/main/tests/dotnet/Runic.CommandLine.Tests),
look up an [`RCLI` diagnostic code](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/docs/guides/command-line/diagnostics.md)
(generator errors and catalog validation messages link to their section),
or [report an issue](https://github.com/Runic-Artifex/runic-cli-sdk/issues).
Runic.CommandLine is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/LICENSE).
