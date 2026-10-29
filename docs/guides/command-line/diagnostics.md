# Runic Command Line diagnostics

This page describes every diagnostic code that the Runic Command Line packages
and the source generator embedded in `Runic.CommandLine` report. Each code has
a section whose anchor is the lowercase code, such as `#rcli9001`. Generator
help links and catalog validation messages open that section at the release
tag of the package you use, so the page always matches your version.

| Codes | Reported by | Where it appears |
|---|---|---|
| `RCLI0002`–`RCLI0026` | Catalog validation | `CommandCatalogValidationException` at startup or in tests |
| `RCLI1001`–`RCLI1021` | Parser and response files | Usage diagnostics (exit code 2) |
| `RCLI2002`–`RCLI2006` | Binding and validation | Usage diagnostics (exit code 2) |
| `RCLI4000`, `RCLI4001`, `RCLI5000` | Execution | Cancellation, unanswerable prompts and host faults |
| `RCLI6001`–`RCLI6011` | `Runic.CommandLine.Processes` | Process faults returned to the command |
| `RCLI9001`–`RCLI9046` | Source generator | Compiler errors and warnings |

Parse and binding diagnostics are written for the people who run your
application, so their output carries no link to this page. Their codes, kinds
and arguments are stable; see the
[grammar](../../../specs/command-line/grammar.md) and
[protocol](../../../specs/command-line/protocol/README.md) specifications.

Codes are for programs. JSON output carries the code of every fault and
diagnostic; human output writes each one as its message alone, and shows a code
only on the `Help for` line of a [help link](#help-links). A usage error (exit
code 2) in human output ends with the usage line of its command and the help
command to run:

```text
The binding argument is missing.
Usage: storyline bind <world> <binding> [options]
Run "storyline bind --help" for more information.
```

Both lines are localizable (`help.usage`, `hints.usage`; see
[text keys](text-keys.md)). They are written by `CommandApp`, its hosting
adapter and their default outcome sink; an application that writes usage errors
itself, through `PresentFrameworkRequest`, the command's `humanOutput` or its
own outcome sink, writes its own text.

`RCLI8000` through `RCLI8999` are reserved for applications and are never
emitted by this library or its generator. An application documents its own
codes and can link a fault or diagnostic to that documentation with
`HelpUri`; see [Help links](#help-links).

A diagnostic code in a `runic.commandline/1` response is always `RCLI` and four
digits, so an application reports its own diagnostics in that range; a fault
code may use any prefix. The Runic tools share the range in blocks of one
hundred:

| Codes | Application |
|---|---|
| `RCLI8100`–`RCLI8199` | `dotnet runic` (Runic SDK) |
| `RCLI8200`–`RCLI8299` | `runic-translations` (Runic Translations) |
| `RCLI8300`–`RCLI8399` | `runic-create` (Runic SDK) |

The rest of the range, `RCLI8000`–`RCLI8099` and `RCLI8400`–`RCLI8999`, is free
for any application. An application never uses codes outside `RCLI8000`–`RCLI8999`:
the other `RCLI` codes belong to this library and its generator.

## Help links

`CommandFault.HelpUri` and `CommandDiagnostic.HelpUri` take an absolute `https`
link to the documentation of the code:

```csharp
return CommandOutcome.Failure<Report>(
    CommandExitCategory.CommandFailure,
    new CommandFault("RAS1001", "The archive could not be read.")
    {
        HelpUri = new Uri("https://docs.example.com/errors#ras1001"),
    });
```

JSON output adds a `helpUri` member to the fault or diagnostic, and human
output writes a line after it:

```text
The archive could not be read.
Help for RAS1001: https://docs.example.com/errors#ras1001
```

Messages, details and arguments are still redacted when they contain URLs,
paths or other technical content; only the help link is exempt. Use a fixed
documentation address, never one built from user input.

## Catalog validation

`CommandCatalogBuilder.Build()`, `GeneratedCommandCatalog.Create()` and
`CommandCatalog.Combine()` throw `CommandCatalogValidationException` when a
definition is invalid. Its message lists each issue with its code, the command
path and a link to its section here; `Issues` holds all of them. The generator
reports most of these mistakes at build time (`RCLI9xxx`), so these codes
mostly appear with hand-written catalogs.

### RCLI0002

Invalid command name or alias. Each space-separated segment of a command name,
and each alias, must start with a lowercase ASCII letter and contain only
lowercase letters, digits and hyphens. `help`, `version`, `output` and `root`
are reserved: the JSON envelope's `command` value is `root` for root-level
responses and `help` or `version` for framework responses (see the
[protocol](../../../specs/command-line/protocol/README.md#reserved-command-values)).

### RCLI0003

Empty description key. A `DescriptionKey` on a command, option or argument is
either omitted (`null`) or a nonempty localization key.

### RCLI0004

Duplicate command spelling. A command name or alias is registered more than
once in a command group, or in two catalogs passed to `CommandCatalog.Combine`.
Rename one of the commands or remove the alias.

### RCLI0005

Invalid option ID. An option ID is the stable identity that bindings,
`Requires` and `ConflictsWith` use. It must start with a lowercase ASCII letter
and contain only lowercase letters, digits and hyphens, such as `dry-run`.

### RCLI0006

Duplicate option ID. Two options of one command share an ID. Give each option
its own ID.

### RCLI0007

Invalid or reserved option spelling. Long options are `--name`, short options
are one ASCII letter such as `-v`, and slash aliases such as `/verbose` are
registered explicitly. `--help`, `-h` and `--version` are reserved.

### RCLI0008

Duplicate option spelling. One spelling, such as `-v`, names two options of a
command, possibly through an alias or a global option. Remove one of them.

### RCLI0009

Invalid repeat policy. The option's `CommandOptionRepeatPolicy` is not a defined
value.

### RCLI0010

Missing options binder. A hand-registered command needs a closed
`ICommandOptionsBinder` that turns the parsed invocation into its options.

### RCLI0011

Missing handler factory. A hand-registered command needs a closed handler
factory that creates the command's handler.

### RCLI0012

Missing result codec. A hand-registered command needs a closed
`ICommandResultCodec<T>` for its result type.

### RCLI0013

Invalid argument name or ID. Positional argument names and IDs follow the same
rule as option IDs: a lowercase ASCII letter followed by lowercase letters,
digits and hyphens.

### RCLI0014

Duplicate argument ID. Two positional arguments of one command share an ID.

### RCLI0015

Required argument after an optional argument. Positional arguments bind in
order, so a required argument cannot follow an optional one. Make the later
argument optional too, or move the required argument first. The generator
reports this as `RCLI9035`.

### RCLI0016

Multi-value argument is not last. An argument that accepts any number of values
consumes the remaining tokens, so it must be the final argument. The generator
reports this as `RCLI9020`.

### RCLI0017

Invalid result payload identity. A result codec's `PayloadType` must be
`<lower-name>/<positive-major>`, such as `sample.export-result/1`.

### RCLI0018

Unknown default command. The default command must name a registered
single-segment root command. The generator reports this as `RCLI9040`. A root
command selected with `CommandCatalogBuilder.RootCommand` or `[RootCommand]` has
no spelling, so it also cannot have aliases or subcommands.

### RCLI0019

Conflicting catalog definitions. Either a global option has the same ID as a
command's own option with a different spelling, arity or aliases, or more than
one catalog passed to `CommandCatalog.Combine` defines a default command. Keep
one definition.

For an option, the issue is located at the command and names the option ID and
both definitions, with the arity when it differs:

```text
RCLI0019 at 'c': Global option 'force' conflicts with the command-local option of the same ID:
the global option is --force with aliases [-F], the command-local option is --force with aliases [-f].
Use the same name, arity and aliases, or give the options different IDs.
```

Alias order does not matter. Give both definitions the same aliases, or rename
the command's option ID if it is a different option.

### RCLI0020

Presenter type mismatch. A human presenter (`Present<T>`) or outcome presenter
(`PresentOutcome<T>`) registered for a command must take the command's result
type.

### RCLI0021

Invalid path or numeric validation metadata. `MustExist` needs a file or
directory path kind, `PathKind` must be a defined value, and `Minimum` and
`Maximum` must be finite with `Minimum` at most `Maximum`. The generator reports
these mistakes as `RCLI9036` and `RCLI9037`.

### RCLI0022

Invalid option relationship. `Requires` and `ConflictsWith` must name another
registered option of the same command by ID, such as `dry-run`, not by
spelling, not itself and not an argument. The generator reports this as
`RCLI9038` or `RCLI9039`.

### RCLI0023

Unresolved description key. `CommandTextContext.ValidateDescriptionKeys`
reports each `DescriptionKey` that the text resolver does not resolve in the
current culture; help then shows the literal description. Add the key to your
text resources or correct its spelling. With `RUNIC_COMMANDLINE_DEBUG=1`,
`CommandApp` writes these issues as warnings when it shows help.

### RCLI0024

JSON output alias conflict. The alias declared with
`CommandCatalogBuilder.JsonOutputAlias`, such as `--json`, is spelled like the
catalog's output selector (`--format` unless `CommandCatalogBuilder.OutputSelector`
declares another) or one of the selector's hidden aliases, is also the name or
an alias of a command option, or catalogs combined with
`CommandCatalog.Combine` declare different aliases or the same alias with
different output shapes (`JsonOutputShape`). The alias is recognized
before any command option, so remove the option, or keep it and do not declare
the alias.

### RCLI0025

Completion option conflict. The option declared with
`CommandCatalogBuilder.CompletionOption`, such as `--completions`, is also the
name or an alias of a command option, is spelled like the output selector, one
of its hidden aliases or the JSON output alias, or catalogs combined with
`CommandCatalog.Combine` declare different completion options. The option is
recognized before the command line is parsed, so rename one of them.

### RCLI0026

Output selector conflict. A command option's name or alias is spelled like the
catalog's output selector or one of its hidden aliases, or catalogs combined
with `CommandCatalog.Combine` declare different output selectors (a different
name or different hidden aliases). The selector is `--format` unless
`CommandCatalogBuilder.OutputSelector` declares another, so an option named
`--format` conflicts by default. The selector is recognized before any command
option, so rename the option, or declare another selector, for example
`OutputSelector("--output")`. A catalog that declares no selector takes the
selector of the catalogs it is combined with.

## Parsing

The parser reports these codes as `parse` diagnostics with the stable `kind`
shown. They map to the usage exit category, exit code 2 by default.

### RCLI1001

`unknown-option`: the command has no option with this spelling. The diagnostic
suggests a close spelling when there is one.

### RCLI1002

`unknown-command`: no command matches the leading tokens. Its argument is the
token given where the command belongs, after any leading output or global
options, and is absent when no command was typed. The diagnostic suggests a
close command name when there is one.

### RCLI1003

`missing-option-value`: an option that takes a value is the last token, or is
followed by another option or `--`.

### RCLI1004

`unexpected-option-value`: a flag that takes no value was given one, as in
`--force=yes`.

### RCLI1005

`missing-argument`: a required positional argument is missing. Arguments bind
left to right, so the diagnostic names the first argument the given tokens do
not reach: `copy a.txt` for `copy <source> <destination>` names `destination`.

### RCLI1006

`unexpected-argument`: more positional tokens were given than the command
accepts.

### RCLI1007

`duplicate-option`: an option that accepts one occurrence was given more than
once.

### RCLI1008

`unsupported-short-bundle`: short options cannot be bundled; write `-a -b`
instead of `-ab`.

### RCLI1010

`invalid-output-mode`: the output selector (`--format`, or the one the catalog
declares with `CommandCatalogBuilder.OutputSelector`), one of its hidden aliases
or `RUNIC_COMMANDLINE_OUTPUT` names a value other than `human` or `json`,
compared case-insensitively. The diagnostic names the selector, also when a
hidden alias was given. The value itself is never echoed.

### RCLI1011

`transport-output-option-collision`: the output selector is also an option of
the matched command. Catalog validation already rejects such a catalog with
`RCLI0026`, so the parser reports this only for a catalog that bypassed it.
Rename the option or declare another selector.

### RCLI1012

`missing-required-option`: a required option was neither given nor supplied by
its environment variable.

### RCLI1013

`unexpected-root-help-argument`: root `help` or `--help` was followed by a token
it does not accept.

### RCLI1014

`invalid-environment-value`: the environment variable of a flag holds a value
other than `true`, `false`, `yes`, `no`, `1` or `0` (case-insensitive).

### RCLI1015

`invalid-choice`: the value is not one of the declared choices. The message
lists the choices unless the option is sensitive or the list is too long.

### RCLI1016

`conflicting-output-selector`: two different spellings that select the output
format were both given: the output selector (`--format`, or the one the
catalog declares), one of its hidden aliases, or the application's JSON output
alias, such as `--json`. Give one of them. The diagnostic names both spellings
and points at the later one. The same spelling given twice is `RCLI1007`, and
the JSON alias with a value, as in `--json=true`, is `RCLI1004`.

### RCLI1017

`response-file-unreadable`: a response file could not be read. It does not
exist, is a directory, cannot be opened, or its name is not a valid path. It
also covers expansion past its bounds: a file of more than 1 MiB, such as
`/dev/zero`, or more than 4 MiB or 65,536 arguments from all response files of
the command line together. A pipe, such as `@/dev/stdin`, is read like a file
within the same bounds. Only
applications that declare `CommandCatalogBuilder.ResponseFiles()` read
response files; see [response files](#response-files). The argument is the
file name as written after `@`, or only its last segment when it names a home
or temporary directory, which output would otherwise redact. The token index
is the command-line argument that named the first response file, also when a
nested file failed.

### RCLI1018

`response-file-invalid-utf8`: a response file is not valid UTF-8. Response
files are read as strict UTF-8, with or without a byte order mark; save the
file as UTF-8.

### RCLI1019

`response-file-unterminated-quote`: a response file ends inside a double
quote. Close the quote, or write a literal quote as `\"`.

### RCLI1020

`response-file-too-deep`: response files name each other more than 16 levels
deep. The argument is the reference that would open the seventeenth file.

### RCLI1021

`response-file-cycle`: a response file names itself, directly or through
other response files. The argument is the reference that closes the cycle.
Naming the same file twice, one after the other, is allowed.

### Response files

These codes appear only for an application whose catalog declares
`CommandCatalogBuilder.ResponseFiles()`. Arguments are expanded before
parsing, so a response file that fails is reported before any parse error,
except an invalid output selection given on the command line itself. Parse
diagnostics after a successful expansion count token indexes in the expanded
arguments.

## Binding and validation

Binding converts parsed values and applies declared validation. These codes are
`binding` diagnostics and map to the usage exit category.

### RCLI2002

Declared validation failed: a value is outside `Minimum` or `Maximum`
(`out-of-range`, `below-minimum`, `above-maximum`), a path is invalid or does
not exist (`invalid-file-path`, `file-not-found` and their directory forms), or
an option's `Requires` or `ConflictsWith` rule is broken (`option-requires`,
`option-conflict`).

### RCLI2005

Conversion failed: a value is not a valid integer, number, GUID, Boolean or
choice (`invalid-integer`, `invalid-number`, `invalid-guid`, `invalid-boolean`,
`invalid-choice`), or a `[ConvertWith]` converter or `[ValidateWith]` validator
rejected it (`invalid-value`, `validation-failed`).

### RCLI2006

`environment-value-source`: an information diagnostic that accompanies
`RCLI2002` or `RCLI2005` when the rejected value came from an option's
environment variable, and names that variable.

## Execution

### RCLI4000

The command was cancelled, for example by Ctrl+C, before it completed. Ctrl+C,
Escape or the end of input while a `CommandPrompts` prompt waits also cancel the
command this way. The exit category is `Cancelled`, exit code 130.

### RCLI4001

A `CommandPrompts` prompt without a default needed an answer, but the command
could not ask for one. The diagnostic kind names the reason:
`prompt-json-output` (JSON output never prompts), `prompt-requires-terminal`
(standard input or standard error, where prompts are written, is not a
terminal; standard output may be redirected) or `prompt-disabled`
(`RUNIC_COMMANDLINE_NO_INPUT` is set, or the handler created the prompts with
`interactive: false`). Its argument is the prompt's question. Pass the value
another way, usually as an option, or give the prompt a default. The exit
category is `Usage`, exit code 2.

### RCLI5000

The command failed unexpectedly: it threw an exception, or its fault had a code
that is not a valid identifier. The exception is not written to the output. Set
`RUNIC_COMMANDLINE_DEBUG=1` to write it to standard error, or log it with
`CommandApp.ExceptionObserver`. `DOTNET_ENVIRONMENT=Development` and an attached
debugger also write it to standard error. That text is stripped of control
characters other than line breaks and tabs, but not redacted: it can contain
paths and secrets, so keep both variables unset where standard error is
collected, such as in CI. The exit category is `HostFailure`, exit code 3.

## Processes

`ProcessRunner` in `Runic.CommandLine.Processes` returns these faults, also
available as `ProcessFaultCodes` constants. Policy messages and details are
normalized so that they do not leak the request's input.

### RCLI6001

The executable policy rejected the executable.

### RCLI6002

The executable policy rejected the working directory.

### RCLI6003

The executable policy returned an invalid decision.

### RCLI6004

The executable policy threw or could not evaluate the request safely.

### RCLI6005

The operating system could not start the process, for example because the
executable does not exist or is not executable.

### RCLI6006

The started process could not be observed safely to completion.

### RCLI6007

A Windows batch file (`.cmd` or `.bat`) was requested without opting in. A
command name such as `npm` can resolve to `npm.cmd`. Set
`allowWindowsBatchFiles: true` on `ProcessExecutionOptions` only for trusted
scripts.

### RCLI6008

A batch file path or argument contains a `cmd.exe` metacharacter
(`% ! ^ & | < > " ( )`) or a line break. These are rejected rather than
escaped. When arguments come from users, run the underlying executable instead,
for example `node` with the package's script path.

### RCLI6009

The output handler passed to `RunAsync` threw while the request was neither
cancelled nor timed out. The runner terminated the child and its descendants,
and the result is `ExecutionFailed`. The handler's exception is not part of the
result; log it in the handler.

### RCLI6010

`ProcessResult.EnsureCompleted` found a request whose timeout elapsed. The
runner already terminated the child and its descendants.

### RCLI6011

`ProcessResult.EnsureCompleted` found incomplete captured output. The fault's
`stream` detail is `standardOutput` or `standardError`, and its `reason` is
`truncated` when the child wrote more than the capture limit, or `drainTimedOut`
when a descendant held the pipe open after the child exited. Raise the limit,
or pass an output handler to process the output as it arrives.

## Source generator

The source generator embedded in `Runic.CommandLine` reports `RCLI9xxx`
compiler errors (and the `RCLI9039` and `RCLI9044` warnings) for `[Command]` methods it cannot
turn into catalog entries. The generator skips an invalid command, so fix every
error before relying on the generated catalog. Release tracking lives in
[`AnalyzerReleases.Shipped.md`](../../../packages/dotnet/Runic.CommandLine.Generators/AnalyzerReleases.Shipped.md).

### RCLI9001

Invalid generated command. A `[Command]` method must be an accessible
(`public` or `internal`), non-generic, ordinary `static` method whose containing
types are accessible and non-generic. Its name must be nonempty, and each
space-separated segment must start with a lowercase ASCII letter and contain
only lowercase letters, digits and hyphens; `help`, `version`, `output` and
`root` are reserved, as for [`RCLI0002`](#rcli0002). Result types are checked separately (`RCLI9028`).

### RCLI9002

Invalid generated command parameter. Each parameter needs exactly one of
`[Argument]`, `[TrailingArguments]`, `[Option]` or `[FromServices]`, unless it is a context parameter
(`CancellationToken`, `CommandExecutionContext` or `ICommandConsole`). Remove the
extra attribute or add the missing one.

### RCLI9003

Unsupported generated command type. The message names the parameter, its
type and the reason it cannot be bound:

- An argument or option binds `string`, `int`, `long`, `double`, `decimal`,
  `bool`, `Guid`, `FileInfo`, `DirectoryInfo`, an enum, a nullable of these, or
  an array or `IReadOnlyList<T>` of them. Add `[ConvertWith]` for another type,
  or use `[FromServices]` to inject it.
- Positional arguments cannot be Boolean; use an option flag.
- `[TrailingArguments]` binds an array or `IReadOnlyList<T>` of those types.
- A `[FromServices]` type and the types that contain it must be `public` or
  `internal`, and containing types must not be generic or file-local.
- The generator cannot resolve the type. Source generators all run against the
  code you wrote and do not see each other's output, so a type that another
  generator emits is unknown to the command generator even though the rest of
  the build compiles against it. A Runic Translations text class such as
  `HelloText` is such a type. Inject a type declared in source or a referenced
  assembly instead; for Runic Translations, inject `ITranslationManager` and
  create the text class in the command:

  ```csharp
  [Command("hello")]
  internal static string Hello([FromServices] ITranslationManager translations) =>
      new HelloText(translations).Messages.greeting;
  ```

  The same message appears, next to `CS0246`, when the type is not declared at
  all.

### RCLI9004

Duplicate generated command name. Two `[Command]` methods declare the same
command path. Rename one of them.

### RCLI9005

Invalid generated command metadata. The message names the invalid item: a
parameter ID, option spelling or alias (including the reserved `--help`, `-h`
and `--version`), duplicate IDs or spellings within a command, a numeric range
or a default outside it, a result payload type, a second `[TrailingArguments]`
parameter of the same command, or a description key that is empty, longer than
128 characters or contains control characters.

### RCLI9020

Multi-value argument is not last. Only the final positional argument may set
`AllowMultipleValues`; move it to the end or make it an option.

### RCLI9021

By-reference command parameter. `ref`, `in`, `out` and `ref readonly`
parameters cannot be bound. Pass the value by value.

### RCLI9022

Default value on an unbound command parameter. A C# default only applies to an
`[Argument]` or `[Option]`. Remove the default or bind the parameter. An injected
`CancellationToken` may declare `= default`, so it can stay last after defaulted
options as CA1068 recommends.

### RCLI9023

Required parameter with a default value. `Required = true` on an `[Option]` or
an `[Argument]` and a C# default contradict each other. Remove one of them.

### RCLI9024

Flag defaults to true. A Boolean option is a presence flag, so a `true` default
could never be turned off. Default to `false` and invert the name (for example
`--no-cache`), or use `bool?` to accept an explicit value.

### RCLI9025

List option with a default value. Array and list options default to empty.
Remove the C# default and apply a fallback in the command body.

### RCLI9026

`AllowMultipleValues` does not match the parameter type. Set it exactly when the
parameter is an array or `IReadOnlyList<T>`.

### RCLI9027

Occurrence policy on a scalar option. `AllowMultipleOccurrences` applies only to
list options; remove it from a scalar option.

### RCLI9028

Missing command result metadata. A command that returns a type other than
`string`, `void`, `Task`, `ValueTask` or `CommandUnit` must declare
`[CommandResult("<name>/<major>", typeof(MyJsonContext))]` so its JSON payload
has an identity and source-generated metadata. The same holds when the result
is wrapped in `Task<T>`, `ValueTask<T>` or `CommandOutcome<T>`: a
`CommandOutcome<CommandUnit>` command needs no metadata and behaves like a
`void` command, with the payload type `runic.unit/1`. An `int` or `long` result is
payload data, not the process exit code: to choose the exit code, return
`CommandOutcome<T>` with a `CommandExitCategory`, or configure an
`IExitCodePolicy`. An empty payload type is reported as `RCLI9005`, an
unusable context as `RCLI9041` and a context without the result type as
`RCLI9042`.

### RCLI9029

Multiple default commands. At most one command may be marked
`[DefaultCommand]` or `[RootCommand]`; every marked command is reported and none
becomes the default.

### RCLI9030

Invalid command converter. The `[ConvertWith]` type must be an accessible,
closed, non-abstract class or struct implementing
`ICommandValueConverter<T>` for the parameter type, with a concrete static
implementation.

### RCLI9031

Invalid command validator. The `[ValidateWith]` type must be an accessible,
closed, non-abstract class or struct implementing `ICommandValueValidator<T>`
for the parameter type, with a concrete static implementation.

### RCLI9032

Converter on a Boolean flag. A Boolean option is a presence flag and takes no
text to convert. Use a value-taking parameter type for text conversion.

### RCLI9033

Conversion metadata on an unbound parameter. `[ConvertWith]` and
`[ValidateWith]` apply only to `[Argument]` and `[Option]` parameters.

### RCLI9034

Converter on a list parameter. List binding converts each element with the
built-in scalar conversions, so `[ConvertWith]` cannot be applied to an array or
list parameter.

### RCLI9035

Required argument after an optional argument. Positional arguments bind in
order, so a required argument cannot follow one that is nullable, has a C#
default or accepts multiple values. Make the later argument optional too, or
move the required argument first. Without this check the catalog would fail
at startup with `RCLI0015`.

### RCLI9036

Numeric bounds on a non-numeric parameter. `Minimum` and `Maximum` apply only
to `int`, `long`, `double` and `decimal` parameters (or arrays, lists and
nullables of them). Remove the bounds, change the parameter type, or validate
the value with `[ValidateWith]`. A parameter with `[ConvertWith]` may keep
bounds; they then check the raw text as a number.

### RCLI9037

Invalid path metadata. `MustExist = true` needs a path kind: use a `FileInfo`
or `DirectoryInfo` parameter, or set `PathKind = CommandPathKind.File` or
`CommandPathKind.Directory` on a string. `PathKind` must be a defined
`CommandPathKind` value. Without this check the catalog would fail at startup
with `RCLI0021`.

### RCLI9038

Invalid option relationship. `Requires` and `ConflictsWith` name other options
by stable ID, not spelling: parameter `dryRun` has ID `dry-run`, not
`--dry-run`. An option cannot name itself or a positional argument. Without
this check the catalog would fail at startup with `RCLI0022`.

### RCLI9039

Option relationship names an unknown option. The ID in `Requires` or
`ConflictsWith` is not an option of the same command method. This is a
warning because a global option added with
`GeneratedCommandCatalog.Create(builder => builder.GlobalOption(...))` is
invisible to the generator; if the ID names such an option, suppress the
warning. Otherwise the catalog fails at startup with `RCLI0022`.

### RCLI9040

Default command is not a root command. `[DefaultCommand]` and `[RootCommand]` apply only to a
single-segment command such as `[Command("list")]`, not to
`[Command("config show")]`. Without this check the catalog would fail at
startup with `RCLI0018`.

### RCLI9041

Invalid command result JSON context. The type in
`[CommandResult(payloadType, typeof(MyJsonContext))]` must be an accessible
(`public` or `internal`) class deriving from `JsonSerializerContext`, with
accessible containing types.

### RCLI9042

JSON context lacks the command result type. Add
`[JsonSerializable(typeof(MyResult))]` for the command's exact result type to
the context named in `[CommandResult]`.

### RCLI9043

Invalid command group description. The path in `[CommandGroup("config")]`
must be a leading part of at least one generated command name, such as
`config` for `[Command("config show")]`; it must not name a command itself, and
each group is described once. A misspelled path would otherwise add an empty
group to help.

### RCLI9044

Command result JSON context does not use camelCase. The context named in
`[CommandResult(payloadType, typeof(MyJsonContext))]` keeps the C# property
names, so the payload is written as `{"Output":…}`. The `runic.commandline/1`
envelope and Runic payloads use camelCase names, which a JSON consumer expects
throughout one response. Declare the policy on the context:

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MyResult))]
internal sealed partial class MyJsonContext : JsonSerializerContext;
```

`[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]` also selects
camelCase. A context without `[JsonSourceGenerationOptions]` counts as not
camelCase. Results without property names, such as numbers, strings, enums or
lists of them, are not reported. Changing the policy renames the payload
properties, which breaks consumers of an existing payload type: keep the names
of a published payload with `#pragma warning disable RCLI9044` or `<NoWarn>`,
and switch to camelCase with the next major of that payload type.

### RCLI9045

Option spelled like the output selector. An `[Option]` of a generated command
is named like the catalog's output selector or one of its hidden aliases, or has
such an alias: `--format` when the assembly declares no other selector, or the
spellings of its `[assembly: OutputSelector(...)]`. Catalog validation would
report [`RCLI0026`](#rcli0026) when the catalog is built at run time. Rename the
option, or let the command keep it and give the selector another spelling:

```csharp
[assembly: OutputSelector("--output-format")]
```

The generated catalog then declares that selector, so `--format` belongs to the
command, and the generator checks options against `--output-format` instead.
A selector declared only at run time, with `CommandCatalogBuilder.OutputSelector(...)`
in `GeneratedCommandCatalog.Create(configure)`, is not visible to the generator,
so this is a warning; prefer the attribute, or suppress the warning where
`configure` declares another selector. Other spellings, including `--output`,
are not reported unless the assembly declares them.

### RCLI9046

Invalid output selector. The spellings of `[assembly: OutputSelector(name,
hiddenAliases)]` must be long option spellings (`--`, a lowercase letter, then
lowercase letters, digits or hyphens) other than `--help` and `--version`, each
given once, as `CommandCatalogBuilder.OutputSelector` requires. The generated
catalog does not declare an invalid selector.
