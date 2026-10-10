; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; When a release ships, move these rows to AnalyzerReleases.Shipped.md under its header; see the
; version mapping there (0.6.0-preview.N is 0.6.0.N, the final 0.6.0 is 0.6.0.1000).

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
