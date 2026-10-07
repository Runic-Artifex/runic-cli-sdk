; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; When a release ships, move these rows to AnalyzerReleases.Shipped.md under its header; see the
; version mapping there (0.6.0-preview.N is 0.6.0.N, the final 0.6.0 is 0.6.0.1000).

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9030 | Runic.CommandLine | Error | Invalid command converter
RCLI9031 | Runic.CommandLine | Error | Invalid command validator
RCLI9032 | Runic.CommandLine | Error | Converter on a Boolean flag
RCLI9033 | Runic.CommandLine | Error | Conversion metadata on an unbound parameter
RCLI9034 | Runic.CommandLine | Error | Converter on a list parameter
