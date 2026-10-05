# Runic Command Line

Runic Command Line is a NativeAOT-friendly .NET command application framework.
It supplies a parser-neutral catalog, typed execution, generated bindings, and
predictable human or JSON output without depending on a UI framework.

Install the core package:

```sh
dotnet add package Runic.CommandLine --prerelease
```

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

The repository pins .NET in `global.json`. On NixOS, enter the pinned shell:

```sh
direnv allow
dotnet restore Runic.CommandLine.slnx
./eng/test.sh
./eng/pack.sh 0.6.0-preview.2 artifacts/packages
./eng/verify-packages.sh 0.6.0-preview.2 artifacts/packages
```

`eng/test.sh` runs the managed suites and examples. `eng/verify.sh` additionally
runs the NativeAOT source smoke and an isolated consumer against the packed
packages. CI owns the cross-platform NativeAOT matrix.

## Releases

The four NuGet package identities release together, independently from the
Runic SDK. See [the release guide](eng/release/README.md). The first standalone
release is `0.6.0-preview.2`; package consumers on earlier SDK previews may
continue to use `0.6.0-preview.1` until they adopt the new release.

## History

This repository was extracted from `Runic-Artifex/runic-sdk` at source commit
`e6de9ca3c6073aae33dc5fadbe6ebd5ec3de58df`. Its history contains only Command
Line product paths, including legacy `WebUIToolkit.CommandLine*` and
`RunicCommandLine*` namespaces. No SDK release tags were carried forward.
