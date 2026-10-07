; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

; Release headers must be System.Version values, so a preview release maps to a fourth component:
; Release 0.6.0.1 is 0.6.0-preview.1, and 0.6.0-preview.N is 0.6.0.N. A stable release uses four
; components ending in a number above every preview of that version, so the final 0.6.0 is
; 0.6.0.1000 (and 0.6.1 is 0.6.1.1000). Headers then sort in release order.
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
