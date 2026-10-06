# Runic Command Line

Runic Command Line is a NativeAOT-friendly .NET command application framework.
It supplies a parser-neutral catalog, typed execution, generated bindings, and
predictable human or JSON output without depending on a UI framework.

## Package-only quick-start

Use .NET 10. Create a console project and install the core package, which includes
the source generator. This method-first example is maintained in
[`hello-world/Program.cs`](examples/command-line/hello-world/Program.cs).

The committed `0.6.0-preview.2` is a **candidate**, not a published-version promise.
On 2026-10-05 NuGet lists `0.6.0-preview.1` as the latest published version;
the tutorial APIs here are verified against the candidate. Download the
`command-line-packages` artifact from a successful CI run and extract it to an
absolute directory, then replace `/absolute/path/to/candidate-feed` below.
No source checkout or project references are needed.

```sh
dotnet new console --framework net10.0 --name HelloCli
cd HelloCli
dotnet add package Runic.CommandLine --version 0.6.0-preview.2 --source /absolute/path/to/candidate-feed
```

Replace `Program.cs` with:

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Generated;

return await new CommandApp(GeneratedCommandCatalog.Create()) { Name = "hello" }.RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Say hello."), DefaultCommand]
    internal static string Greet([Argument] string name = "world",
        [Option("--count", "-n", Minimum = 1, Maximum = 100)] int count = 1) =>
        string.Join('\n', Enumerable.Repeat($"Hello, {name}!", count));
}
```

Run the same command in human and machine mode:

```sh
dotnet run -- Ada --count 2
dotnet run -- Ada --count 2 --output=json
dotnet run -- --help
```

Human output is two `Hello, Ada!` lines. JSON output is one `runic.commandline/1`
envelope with `success: true` and a string `payload` containing those lines.
The built-in string codec supplies both formats; no JSON context is needed here.

After the candidate is published, install that exact version from NuGet by
omitting `--source`. To use an already published release now, choose a version
listed on [NuGet](https://www.nuget.org/packages/Runic.CommandLine) and consult
that package's README for its supported API. Optional packages must use the same
Command Line version.

| Package | Purpose |
| --- | --- |
| `Runic.CommandLine` | Contracts, catalog, parsing, execution, hosting, output, and embedded source generator. |
| `Runic.CommandLine.Processes` | Bounded local process execution. |
| `Runic.CommandLine.Spectre` | Spectre.Console presentation adapter. |
| `Runic.CommandLine.Testing` | Deterministic in-memory test helpers. |

Start with [the examples](examples/command-line/README.md) and the
[protocol and grammar specifications](specs/command-line/README.md). Each
published package also contains its own API-oriented README.

## Develop

The repository pins .NET in `global.json`. On NixOS, inspect `.envrc` and
`flake.nix`, then use the pinned environment explicitly:

```sh
direnv allow
direnv exec . ./eng/verify.sh
```

`eng/test.sh` runs the managed suites and examples. `eng/verify.sh` additionally
runs the NativeAOT source smoke and an isolated consumer against the packed
packages. It reads the candidate version from `eng/Versions.props`, or accepts
`./eng/verify.sh <version> [package-directory] [runtime-identifier]`. NativeAOT
defaults to the host runtime. CI packs once and runs source and candidate consumer
NativeAOT checks on Linux x64, Windows x64 and macOS Arm64, one native job at a time.

Each package tracks its public API in `PublicAPI.Shipped.txt` (the last published
release) and `PublicAPI.Unshipped.txt` (changes since then), checked by
`Microsoft.CodeAnalysis.PublicApiAnalyzers`. A verification build fails on an
undeclared API change: add new members to `PublicAPI.Unshipped.txt` (the IDE code
fix for RS0016 does this) and mark removals with `*REMOVED*`. Packing also runs
package validation against the release in
`RunicCommandLinePackageValidationBaselineVersion` (`eng/Versions.props`). After a
release, move the unshipped entries into the shipped files and advance the baseline.

Changes reach `main` through pull requests. The `verify` job in
[CI](.github/workflows/ci.yml) always reports and passes only when every other CI
job passed; it is the check to require before merging. CI also lints workflows
with actionlint and requires remote actions pinned to a full commit SHA followed by
a `# vX.Y.Z` comment; update both together from the tag's commit. Report
vulnerabilities as described in [SECURITY.md](SECURITY.md).

## Releases

The four NuGet package identities release together, independently from the
Runic SDK. See [the release guide](eng/release/README.md). The first standalone
release candidate is `0.6.0-preview.2`; package consumers on earlier SDK previews may
continue to use `0.6.0-preview.1` until they adopt the new release.

## History

This repository was extracted from `Runic-Artifex/runic-sdk` at source commit
`e6de9ca3c6073aae33dc5fadbe6ebd5ec3de58df`. Its history contains only Command
Line product paths, including legacy `WebUIToolkit.CommandLine*` and
`RunicCommandLine*` namespaces. No SDK release tags were carried forward.
