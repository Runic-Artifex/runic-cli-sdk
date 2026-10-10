# Runic.CommandLine

`Runic.CommandLine` is the command kernel for .NET 10 applications that need
portable command behavior and predictable output. Define an immutable catalog,
bind parsed values into typed options, execute a closed handler, and emit human
text or a machine-readable response without coupling the public model to a
parser, Generic Host, or UI framework.

## Install

```bash
dotnet add package Runic.CommandLine --prerelease
```

The package targets `net10.0` and includes its portable contracts and host
launch classification. It is a preview package: test updates before adopting
them in a production command contract.

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

`hello`, `hello Ada`, and `hello greet Ada` execute the default command.
`hello help greet` and `hello greet --help` show catalog help. `hello --output json
greet Ada` emits one versioned response. An application without a default command
shows help for an empty invocation; the lower-level parser retains its existing
empty-input classification for hosted UI launch decisions.

See the runnable [command-line example](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/examples/command-line/README.md).

## Method-first inputs and results

- Scalars: strings, integers, long integers, decimals, doubles, GUIDs, enums and
  nullable scalars. Boolean options are presence flags; nullable Boolean options
  accept an explicit Boolean value.
- C# defaults and nullable inputs are optional. A non-nullable scalar option with
  no default is required. `Required = true` explicitly requires flags and lists.
- Arrays and `IReadOnlyList<T>` support the scalar types above. Options append one
  value per occurrence; `AllowMultipleValues = true` also accepts several values
  after one occurrence. A variadic positional must be last.
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
  `GeneratedCommandCatalog.Create(builder => builder.GlobalOption("verbose",
  "--verbose", CommandArity.Zero))` adds an option to all commands; it may precede
  the command. Read shared bindings from `ParsedInvocation.Options` in a binder.
- `[ConvertWith(typeof(Converter))]` selects an `ICommandValueConverter<T>` with
  static `Parse(string)`. `[ValidateWith(typeof(Validator))]` selects an
  `ICommandValueValidator<T>` with static `IsValid(T)`. Converted values are stored
  once during binding and reused by the handler.
- `string`, `void`, `Task` and `ValueTask` results have built-in codecs. Typed
  application payloads retain `[CommandResult("example.result/1", typeof(JsonContext))]`
  and source-generated JSON metadata. An `int` result is payload data, never an
  implicit exit code. Use `CommandOutcome<T>` and an exit policy for domain exits.

CancellationToken, CommandExecutionContext and ICommandConsole parameters are
injected automatically; other services use `[FromServices]`. Instance methods and
runtime assembly discovery are intentionally outside the generated model.

## Presentation and testing

`CommandApp` owns parsing, output environment selection, help, version, process
signals, execution and exit mapping. Configure `ScopeFactory`, `ExitCodePolicy`,
`OutcomeSink`, or `PresentFrameworkRequest` to preserve existing application
contracts during migration.

Ctrl+C (SIGINT), SIGTERM and SIGQUIT (Ctrl+Break on Windows) cancel the
invocation's token, so a handler that honors it stops gracefully and returns the
`Cancelled` exit code; service managers that send SIGTERM get the same graceful
stop. A second signal while the invocation still runs exits the process at once
with 128 plus the signal number (130 for Ctrl+C, 143 for SIGTERM, 131 for
SIGQUIT). Set `HandleCancelKeyPress = false` when an embedding host already owns
process signals; the runtime's default signal handling then applies.

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

JSON output reserves stdout for the envelope. The injected handler console routes
incidental output to stderr and disables reads. Direct process-global Console
writes remain the application's responsibility. `ExceptionObserver` receives
internal failures for application logging while public faults remain sanitized.
Fault text is checked with a heuristic: the case-sensitive substrings
`Exception`, `/home/`, `/Users/`, `/root/`, `/tmp/` and `\\`, and drive-letter
paths such as `C:\` or `c:/`. A fault whose message matches keeps its
well-formed code (for example `RCLI8001` or `RAS1001`); only the message is
replaced with `The command failed; details were redacted.`. Matching detail
values become `[redacted]`, matching detail keys are dropped, and a malformed
code becomes `RCLI5000`. Other paths such as `/var/folders`, `/srv` or
`/nix/store` are not detected, so keep them out of faults yourself.

An exception from a handler, binder or scope becomes the sanitized `RCLI5000`
fault. While developing, set `RUNIC_COMMANDLINE_DEBUG=1` or
`DOTNET_ENVIRONMENT=Development`, or attach a debugger: `CommandApp` then also
writes each observed exception, with its stack trace, to stderr before the
fault line. The JSON frame on stdout is unchanged. Without an
`ExceptionObserver`, the human `RCLI5000` line ends with a hint naming
`RUNIC_COMMANDLINE_DEBUG` (key `faults.RCLI5000.hint`); JSON output never
carries it. Debug detection reads `ParseSettings.GetEnvironmentVariable`, or the
process environment when `ParseSettings` is not set.

### Declared domain failure and recovery data

When a failed or cancelled command must return an exact retained directory or
target identity, declare a bounded domain DTO separately from presentation text:

```csharp
var recovery = CommandFailureData.Create(
    "sample.clone-recovery/1", report, RecoveryJsonContext.Default.CloneRecovery);
return CommandOutcome.FailureWithData<CloneResult>(
    CommandExitCategory.Cancelled,
    new CommandFault("CLONE_CANCELLED", "The clone was cancelled. Inspect the retained directory."),
    recovery,
    humanOutput: "Inspect the retained directory before retrying.\n");
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

`completion bash|zsh|fish|powershell` generates context-aware completion scripts
from the catalog. Set `CompletionExecutableName` when the help-facing name contains
spaces (for example, `dotnet runic`). Candidates follow the current command path,
its visible options and the choices or path hints for the current value; the
scripts do not perform service lookups. See
[discovery, validation and custom results](#discovery-validation-and-custom-results)
for the shell details.

`Runic.CommandLine.Testing` supplies `CommandAppTester`, a configurable
`TestCommandConsole` with queued input, and strict JSON frame assertions. Supply
explicit ParseSettings to keep environment-dependent tests deterministic.

### Migrating from 0.2

Existing explicit catalogs, payload identities and exit policies continue to work.
Replace duplicated startup with `CommandApp` progressively. Retain domain outcome
sinks and custom framework presenters when scripts depend on an existing envelope.

Generated nullable inputs now bind absence as null, optional positional defaults
are honored, and required non-nullable scalar options fail during parsing. Binding
errors identify the parameter and expected type without echoing its value. `help
<command-path>`, prefix output selection, and empty default-command invocations
are now accepted. The version-1 machine envelope has not changed.

For applications with their own `--output` option, keep
`new ParseSettings(transportOutputOptionName: "--runic-output")`. Set its
`GetEnvironmentVariable` callback if that application also declares parameter
environment fallbacks. `ParseSettings` itself never reads the process environment.

## Register and run a command

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
explicit `--output human` or `--output json` value, which takes precedence over
the captured environment value.

## When to use it

Choose this package for the command model, host launch classification, and
execution pipeline. Add
[`Runic.CommandLine.Processes`](https://www.nuget.org/packages/Runic.CommandLine.Processes)
The portable contracts remain directly available from this package when your
own integration exposes or implements them.

Catalog validation reports invalid names, duplicate spellings, invalid arity,
and incomplete registrations together in deterministic definition order.
Execution creates and disposes exactly one scope for each valid invocation; a
success is the only semantic outcome that maps to exit code zero.

## Documentation and support

Read the [Runic Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line/),
see [examples](https://github.com/Runic-Artifex/runic-cli-sdk/tree/main/tests/dotnet/Runic.CommandLine.Tests),
look up a [source generator diagnostic](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/docs/guides/command-line/diagnostics.md)
(each `RCLI9xxx` error links to its section),
or [report an issue](https://github.com/Runic-Artifex/runic-cli-sdk/issues).
Runic.CommandLine is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/LICENSE).

## Commands inside a Runic application

Use `CommandLineHostingAdapter` when the application already owns startup,
services, cancellation and the choice between CLI and UI. It uses the same
catalog, generated binders, executor and framework presentation as `CommandApp`.
It does not start a Generic Host, subscribe to Ctrl+C, or dispose your application.

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Spectre;

// applicationCommandScopes supplies the application's services to command handlers.
var cli = new CommandLineHostingAdapter(catalog, new CommandExecutor(applicationCommandScopes))
{
    Presentation = new()
    {
        Name = "my-app",
        Version = version,
        HelpPresenter = new SpectreHelpPresenter(),
        ExceptionObserver = RecordInternalException,
    },
};
var console = new SpectreCommandConsole();
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
if (decision.Kind == HostedCommandLineDecisionKind.UserInterface)
    return await OpenApplicationUiAsync(applicationStopping);
if (!decision.CanExecute)
    return await cli.PresentAsync(decision, console, culture, correlationId, applicationStopping);
return (await cli.ExecuteAsync(new(decision, console, culture, correlationId, new CommandOutputDispatcher())
{
    ExceptionObserver = RecordInternalException,
}, applicationStopping)).ExitCode;
```

The application captures only the environment values its options declare.
`EnvironmentVariables` copies that dictionary; missing keys never fall back to the
process environment. Explicit arguments override captured values, which override
handler defaults. Output-format selection continues to use the separate captured
`outputEnvironmentValue`. Names in the parameter snapshot are matched exactly.

`Classify` creates no service scope. `PresentAsync` handles scoped help, version,
usage failures and `completion bash|zsh|fish|powershell`, also without a scope.
Help and errors respect human/JSON output selection; completion intentionally
writes the raw script, the same context-aware script `CommandApp` produces. Existing hosts
may keep their own presenters and use the decision's public diagnostics instead.
`PresentAsync` accepts framework decisions created by that same adapter and
rejects UI and invocation decisions. It is available on the concrete adapter;
the existing `IHostedCommandLineAdapter` classify/execute contract is unchanged.

`ExecuteAsync` owns only the invocation scope returned by your scope factory.
Use `[FromServices]` for handler dependencies. Pass your host's cancellation token
and set `ExceptionObserver` to your internal logging callback; exception details
stay out of public faults. If you customize exit codes, supply the same policy to
`CommandExecutor` and `Presentation.ExitCodePolicy`. An explicit empty-input UI
policy wins even when the catalog declares a default command.

See the runnable [hosted example](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/examples/command-line/HostedExample.cs)
for service injection and the complete launch flow. The example's UI branch is a
console placeholder for an application's existing UI launcher.

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
built-in script command. This works for manual and generated catalogs. Built-in
scripts use the configured transport selector (for example `--runic-output`).
With a custom selector, they retain `--output` only if it is an actual catalog
option. Direct callers can use
`CommandCompletion.Generate(catalog, executable, shell, outputOptionName)`.

When a global option reuses a command-local definition, its name, arity and alias
set must match. Alias ordering is irrelevant; mismatches fail catalog validation
with `RCLI0019`, rather than producing command-dependent parsing behavior.

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

`CommandCompletion.Query(catalog, words, outputOptionName)` provides the same
context query for integrations. Exclude the executable from `words`; the final
word is the current prefix, or an empty string after trailing whitespace. Existing
`Generate` overloads remain available.
See the [complete examples](https://github.com/Runic-Artifex/runic-cli-sdk/blob/main/examples/command-line/README.md).

## Explicit culture and localized presentation

Set `CommandApp.Culture` and `CommandApp.TextResolver` to resolve help and
framework/execution diagnostics without selecting a localization library. The
culture defaults to `CultureInfo.CurrentCulture` captured when an invocation
starts; framework prose remains English when no resolver is installed. Direct
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
Value errors use `diagnostics.{kind}` keys such as `diagnostics.invalid-integer`,
`diagnostics.invalid-number`, `diagnostics.invalid-choice` (or
`diagnostics.invalid-choice-hidden` without the list), `diagnostics.out-of-range`,
`diagnostics.below-minimum`, `diagnostics.above-maximum`,
`diagnostics.environment-value-source`, `diagnostics.invalid-output-mode` and
`diagnostics.invalid-environment-value`; argument `{0}` is the option spelling
or argument placeholder. `CommandTextContext.ValidateDescriptionKeys(catalog)`
reports each `DescriptionKey` the resolver does not resolve (`RCLI0023`), so a
test can catch a typo that would otherwise show the literal fallback; with
`RUNIC_COMMANDLINE_DEBUG=1`, help also prints them to stderr as warnings.
Translations change presentation text, including JSON messages, while command
spellings, canonical paths, diagnostic codes/keys and protocol/payload identities
remain unchanged. The existing sanitizers still apply to resolved text.

See the maintained [English/German example](../../../examples/command-line/localized/README.md)
using public, independently versioned Translations packages. It is optional;
this package does not reference Translations.

Converters and validators must be closed, accessible class or struct types
implementing `ICommandValueConverter<T>` or `ICommandValueValidator<T>` for the
exact parameter type, with concrete static implementations. Generator diagnostics
point to invalid attributes. Boolean options are presence flags: they support
validators but reject converters. List binding converts supported element types
and rejects aggregate converters; list validators receive the full bound list.
