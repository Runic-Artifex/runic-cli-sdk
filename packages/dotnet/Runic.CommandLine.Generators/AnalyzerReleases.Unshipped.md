; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; When a release ships, move these rows to AnalyzerReleases.Shipped.md under its header, the plain
; version itself (## Release 0.11.0 for the next release). Release candidates have no header, so from
; 1.0.0-rc.1 rows stay here until 1.0.0 ships and then move under ## Release 1.0.0
; (eng/release/README.md, Release candidates and 1.0.0).

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RCLI9046 | Runic.CommandLine | Error | Invalid output selector
