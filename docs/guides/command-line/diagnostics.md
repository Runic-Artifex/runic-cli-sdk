# Runic Command Line diagnostics

This page describes every diagnostic code that the Runic Command Line packages
and the source generator embedded in `Runic.CommandLine` report. Each code has
a section whose anchor is the lowercase code, such as `#rcli9001`. Generator
help links and catalog validation messages open that section at the release
tag of the package you use, so the page always matches your version.

| Codes | Reported by | Where it appears |
|---|---|---|
| `RCLI0002`–`RCLI0023` | Catalog validation | `CommandCatalogValidationException` at startup or in tests |
| `RCLI1001`–`RCLI1015` | Parser | Usage diagnostics (exit code 2) |
| `RCLI2002`–`RCLI2006` | Binding and validation | Usage diagnostics (exit code 2) |
| `RCLI4000`, `RCLI5000` | Execution | Cancellation and host faults |
| `RCLI6001`–`RCLI6008` | `Runic.CommandLine.Processes` | Process faults returned to the command |
| `RCLI9001`–`RCLI9043` | Source generator | Compiler errors and warnings |

Parse and binding diagnostics are written for the people who run your
application, so their output carries no link to this page. Their codes, kinds
and arguments are stable; see the
[grammar](../../../specs/command-line/grammar.md) and
[protocol](../../../specs/command-line/protocol/README.md) specifications.

`RCLI8000` through `RCLI8999` are reserved for applications and are never
emitted by this library or its generator. An application documents its own
codes and can link a fault or diagnostic to that documentation with
`HelpUri`; see [Help links](#help-links).

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
RAS1001: The archive could not be read.
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
lowercase letters, digits and hyphens. `help`, `version` and `output` are
reserved.

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
single-segment root command. The generator reports this as `RCLI9040`.

### RCLI0019

Conflicting catalog definitions. Either a global option has the same ID as a
command's own option with a different spelling, arity or aliases, or more than
one catalog passed to `CommandCatalog.Combine` defines a default command. Keep
one definition.

### RCLI0020

Presenter type mismatch. A human presenter registered for a command must render
the command's result type.

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

## Parsing

The parser reports these codes as `parse` diagnostics with the stable `kind`
shown. They map to the usage exit category, exit code 2 by default.

### RCLI1001

`unknown-option`: the command has no option with this spelling. The diagnostic
suggests a close spelling when there is one.

### RCLI1002

`unknown-command`: no command matches the leading tokens. The diagnostic
suggests a close command name when there is one.

### RCLI1003

`missing-option-value`: an option that takes a value is the last token, or is
followed by another option or `--`.

### RCLI1004

`unexpected-option-value`: a flag that takes no value was given one, as in
`--force=yes`.

### RCLI1005

`missing-argument`: a required positional argument is missing.

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

`invalid-output-mode`: the output selector (`--output`, or the host's
transport option) or `RUNIC_COMMANDLINE_OUTPUT` names a value other than
`human` or `json`, compared case-insensitively. The value itself is never
echoed.

### RCLI1011

`transport-output-option-collision`: a hosted application's transport output
option, such as `--runic-output`, is also an option of the matched command.
Rename one of them.

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

The command was cancelled, for example by Ctrl+C, before it completed. The exit
category is `Cancelled`.

### RCLI5000

The command failed unexpectedly: it threw an exception, or its fault had a code
that is not a valid identifier. The exception is not written to the output. Set
`RUNIC_COMMANDLINE_DEBUG=1` to write it to standard error, or log it with
`CommandApp.ExceptionObserver`. The exit category is `HostFailure`, exit code 70.

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

## Source generator

The source generator embedded in `Runic.CommandLine` reports `RCLI9xxx`
compiler errors (and the `RCLI9039` warning) for `[Command]` methods it cannot
turn into catalog entries. The generator skips an invalid command, so fix every
error before relying on the generated catalog. Release tracking lives in
[`AnalyzerReleases.Shipped.md`](../../../packages/dotnet/Runic.CommandLine.Generators/AnalyzerReleases.Shipped.md).

### RCLI9001

Invalid generated command. A `[Command]` method must be an accessible
(`public` or `internal`), non-generic, ordinary `static` method whose containing
types are accessible and non-generic. Its name must be nonempty, and each
space-separated segment must start with a lowercase ASCII letter and contain
only lowercase letters, digits and hyphens; `help`, `version` and `output` are
reserved. Result types are checked separately (`RCLI9028`).

### RCLI9002

Invalid generated command parameter. Each parameter needs exactly one of
`[Argument]`, `[Option]` or `[FromServices]`, unless it is a context parameter
(`CancellationToken`, `CommandExecutionContext` or `ICommandConsole`). Remove the
extra attribute or add the missing one.

### RCLI9003

Unsupported generated command type. The parameter type cannot be bound: use a
supported scalar, enum, nullable scalar, array or `IReadOnlyList<T>` of those,
add `[ConvertWith]` for another type, or make a `[FromServices]` type
accessible. Positional arguments cannot be Boolean; use an option flag.

### RCLI9004

Duplicate generated command name. Two `[Command]` methods declare the same
command path. Rename one of them.

### RCLI9005

Invalid generated command metadata. The message names the invalid item: a
parameter ID, option spelling or alias (including the reserved `--help`, `-h`
and `--version`), duplicate IDs or spellings within a command, a numeric range
or a default outside it, a result payload type, or a description key that is
empty, longer than 128 characters or contains control characters.

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

Required option with a default value. `Required = true` and a C# default
contradict each other. Remove one of them.

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
`string` or no value must declare
`[CommandResult("<name>/<major>", typeof(MyJsonContext))]` so its JSON payload
has an identity and source-generated metadata. An `int` or `long` result is
payload data, not the process exit code: to choose the exit code, return
`CommandOutcome<T>` with a `CommandExitCategory`, or configure an
`IExitCodePolicy`. An empty payload type is reported as `RCLI9005`, an
unusable context as `RCLI9041` and a context without the result type as
`RCLI9042`.

### RCLI9029

Multiple default commands. At most one command may be marked
`[DefaultCommand]`; every marked command is reported and none becomes the
default.

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

Default command is not a root command. `[DefaultCommand]` applies only to a
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
