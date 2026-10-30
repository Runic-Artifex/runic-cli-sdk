; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

; Release headers must be System.Version values. From 0.8.0 a release has a plain version, which is its own
; header: ## Release 0.8.0. The earlier preview releases map X.Y.Z-preview.N to the fourth component X.Y.Z.N:
; Release 0.6.0.1 is 0.6.0-preview.1 and 0.7.0.2 is 0.7.0-preview.2. Headers sort in release order, since
; 0.8.0 sorts after every 0.7.0.N.
; Releases up to 0.6.0-preview.1 were built in the runic-sdk repository; each rule is listed under the
; first published Runic.CommandLine package whose generator contained it.

## Release 0.2.0.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9001 | Runic.CommandLine | Error | Invalid generated command
RCLI9002 | Runic.CommandLine | Error | Invalid generated command parameter
RCLI9003 | Runic.CommandLine | Error | Unsupported generated command type
RCLI9004 | Runic.CommandLine | Error | Duplicate generated command name
RCLI9005 | Runic.CommandLine | Error | Invalid generated command metadata

## Release 0.6.0.1

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9020 | Runic.CommandLine | Error | Multi-value argument is not last
RCLI9021 | Runic.CommandLine | Error | By-reference command parameter
RCLI9022 | Runic.CommandLine | Error | Default value on an unbound command parameter
RCLI9023 | Runic.CommandLine | Error | Required option with a default value
RCLI9024 | Runic.CommandLine | Error | Flag defaults to true
RCLI9025 | Runic.CommandLine | Error | List option with a default value
RCLI9026 | Runic.CommandLine | Error | AllowMultipleValues does not match the parameter type
RCLI9027 | Runic.CommandLine | Error | Occurrence policy on a scalar option
RCLI9028 | Runic.CommandLine | Error | Invalid generated command result metadata
RCLI9029 | Runic.CommandLine | Error | Multiple default commands

## Release 0.6.0.2

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9030 | Runic.CommandLine | Error | Invalid command converter
RCLI9031 | Runic.CommandLine | Error | Invalid command validator
RCLI9032 | Runic.CommandLine | Error | Converter on a Boolean flag
RCLI9033 | Runic.CommandLine | Error | Conversion metadata on an unbound parameter
RCLI9034 | Runic.CommandLine | Error | Converter on a list parameter

## Release 0.6.0.4

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9035 | Runic.CommandLine | Error | Required argument after an optional argument
RCLI9036 | Runic.CommandLine | Error | Numeric bounds on a non-numeric parameter
RCLI9037 | Runic.CommandLine | Error | Invalid path metadata
RCLI9038 | Runic.CommandLine | Error | Invalid option relationship
RCLI9039 | Runic.CommandLine | Warning | Option relationship names an unknown option
RCLI9040 | Runic.CommandLine | Error | Default command is not a root command
RCLI9041 | Runic.CommandLine | Error | Invalid command result JSON context
RCLI9042 | Runic.CommandLine | Error | JSON context lacks the command result type
RCLI9043 | Runic.CommandLine | Error | Invalid command group description

## Release 0.8.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9044 | Runic.CommandLine | Warning | Command result JSON context does not use camelCase
RCLI9045 | Runic.CommandLine | Warning | Option spelled like the default output selector

## Release 0.11.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9046 | Runic.CommandLine | Error | Invalid output selector
