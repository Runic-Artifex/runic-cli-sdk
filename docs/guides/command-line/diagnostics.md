# Source generator diagnostics

The source generator embedded in `Runic.CommandLine` reports `RCLI9xxx`
compiler errors for `[Command]` methods it cannot turn into catalog entries.
Each diagnostic's help link opens its section on this page. The generator
skips an invalid command, so fix every error before relying on the generated
catalog. Release tracking lives in
[`AnalyzerReleases.Shipped.md`](../../../packages/dotnet/Runic.CommandLine.Generators/AnalyzerReleases.Shipped.md).

Runtime parse, binding and execution diagnostics use other `RCLI` codes; see the
[grammar](../../../specs/command-line/grammar.md) and
[protocol](../../../specs/command-line/protocol/README.md) specifications.
`RCLI8000` through `RCLI8999` are reserved for applications and are never
emitted by this library or its generator.

## RCLI9001

Invalid generated command. A `[Command]` method must be an accessible
(`public` or `internal`), non-generic, ordinary `static` method whose containing
types are accessible and non-generic. Its name must be nonempty, and each
space-separated segment must start with a lowercase ASCII letter and contain
only lowercase letters, digits and hyphens; `help`, `version` and `output` are
reserved. Result types are checked separately (`RCLI9028`).

## RCLI9002

Invalid generated command parameter. Each parameter needs exactly one of
`[Argument]`, `[Option]` or `[FromServices]`, unless it is a context parameter
(`CancellationToken`, `CommandExecutionContext` or `ICommandConsole`). Remove the
extra attribute or add the missing one.

## RCLI9003

Unsupported generated command type. The parameter type cannot be bound: use a
supported scalar, enum, nullable scalar, array or `IReadOnlyList<T>` of those,
add `[ConvertWith]` for another type, or make a `[FromServices]` type
accessible. Positional arguments cannot be Boolean; use an option flag.

## RCLI9004

Duplicate generated command name. Two `[Command]` methods declare the same
command path. Rename one of them.

## RCLI9005

Invalid generated command metadata. The message names the invalid item: a
parameter ID, option spelling or alias (including the reserved `--help`, `-h`
and `--version`), duplicate IDs or spellings within a command, a numeric range
or a default outside it, a result payload type, or a description key that is
empty, longer than 128 characters or contains control characters.

## RCLI9020

Multi-value argument is not last. Only the final positional argument may set
`AllowMultipleValues`; move it to the end or make it an option.

## RCLI9021

By-reference command parameter. `ref`, `in`, `out` and `ref readonly`
parameters cannot be bound. Pass the value by value.

## RCLI9022

Default value on an unbound command parameter. A C# default only applies to an
`[Argument]` or `[Option]`. Remove the default or bind the parameter.

## RCLI9023

Required option with a default value. `Required = true` and a C# default
contradict each other. Remove one of them.

## RCLI9024

Flag defaults to true. A Boolean option is a presence flag, so a `true` default
could never be turned off. Default to `false` and invert the name (for example
`--no-cache`), or use `bool?` to accept an explicit value.

## RCLI9025

List option with a default value. Array and list options default to empty.
Remove the C# default and apply a fallback in the command body.

## RCLI9026

`AllowMultipleValues` does not match the parameter type. Set it exactly when the
parameter is an array or `IReadOnlyList<T>`.

## RCLI9027

Occurrence policy on a scalar option. `AllowMultipleOccurrences` applies only to
list options; remove it from a scalar option.

## RCLI9028

Missing command result metadata. A command that returns a type other than
`string` or no value must declare
`[CommandResult("<name>/<major>", typeof(MyJsonContext))]` so its JSON payload
has an identity and source-generated metadata. An `int` or `long` result is
payload data, not the process exit code: to choose the exit code, return
`CommandOutcome<T>` with a `CommandExitCategory`, or configure an
`IExitCodePolicy`. An empty payload type is reported as `RCLI9005`, an
unusable context as `RCLI9041` and a context without the result type as
`RCLI9042`.

## RCLI9029

Multiple default commands. At most one command may be marked
`[DefaultCommand]`; every marked command is reported and none becomes the
default.

## RCLI9030

Invalid command converter. The `[ConvertWith]` type must be an accessible,
closed, non-abstract class or struct implementing
`ICommandValueConverter<T>` for the parameter type, with a concrete static
implementation.

## RCLI9031

Invalid command validator. The `[ValidateWith]` type must be an accessible,
closed, non-abstract class or struct implementing `ICommandValueValidator<T>`
for the parameter type, with a concrete static implementation.

## RCLI9032

Converter on a Boolean flag. A Boolean option is a presence flag and takes no
text to convert. Use a value-taking parameter type for text conversion.

## RCLI9033

Conversion metadata on an unbound parameter. `[ConvertWith]` and
`[ValidateWith]` apply only to `[Argument]` and `[Option]` parameters.

## RCLI9034

Converter on a list parameter. List binding converts each element with the
built-in scalar conversions, so `[ConvertWith]` cannot be applied to an array or
list parameter.

## RCLI9035

Required argument after an optional argument. Positional arguments bind in
order, so a required argument cannot follow one that is nullable, has a C#
default or accepts multiple values. Make the later argument optional too, or
move the required argument first. Without this check the catalog would fail
at startup with `RCLI0015`.

## RCLI9036

Numeric bounds on a non-numeric parameter. `Minimum` and `Maximum` apply only
to `int`, `long`, `double` and `decimal` parameters (or arrays, lists and
nullables of them). Remove the bounds, change the parameter type, or validate
the value with `[ValidateWith]`. A parameter with `[ConvertWith]` may keep
bounds; they then check the raw text as a number.

## RCLI9037

Invalid path metadata. `MustExist = true` needs a path kind: use a `FileInfo`
or `DirectoryInfo` parameter, or set `PathKind = CommandPathKind.File` or
`CommandPathKind.Directory` on a string. `PathKind` must be a defined
`CommandPathKind` value. Without this check the catalog would fail at startup
with `RCLI0021`.

## RCLI9038

Invalid option relationship. `Requires` and `ConflictsWith` name other options
by stable ID, not spelling: parameter `dryRun` has ID `dry-run`, not
`--dry-run`. An option cannot name itself or a positional argument. Without
this check the catalog would fail at startup with `RCLI0022`.

## RCLI9039

Option relationship names an unknown option. The ID in `Requires` or
`ConflictsWith` is not an option of the same command method. This is a
warning because a global option added with
`GeneratedCommandCatalog.Create(builder => builder.GlobalOption(...))` is
invisible to the generator; if the ID names such an option, suppress the
warning. Otherwise the catalog fails at startup with `RCLI0022`.

## RCLI9040

Default command is not a root command. `[DefaultCommand]` applies only to a
single-segment command such as `[Command("list")]`, not to
`[Command("config show")]`. Without this check the catalog would fail at
startup with `RCLI0018`.

## RCLI9041

Invalid command result JSON context. The type in
`[CommandResult(payloadType, typeof(MyJsonContext))]` must be an accessible
(`public` or `internal`) class deriving from `JsonSerializerContext`, with
accessible containing types.

## RCLI9042

JSON context lacks the command result type. Add
`[JsonSerializable(typeof(MyResult))]` for the command's exact result type to
the context named in `[CommandResult]`.
