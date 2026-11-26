# Old-name guards

The old-name guards (D079.11) fail a build that still uses a name the Runic Artifex rename replaced. They ship in
`buildTransitive/Runic.CommandLine.RenamedNames.targets` of `Runic.CommandLine`, which
`buildTransitive/Runic.CommandLine.targets` imports, so every Runic Command Line package's consumer gets them, through
1.0.x, and are removed in 2.0.

| Kind | Reported when | Code |
| --- | --- | --- |
| `packages` | an old package ID is in the package graph: a `PackageReference`, or a package NuGet resolved (`PackageDependencies` after `ResolvePackageAssets`) | `RCLI7001` |
| `msbuild` | the project, its imports or the command line sets an old public property or item | `RCLI7002` |
| `environment` | an old environment variable is set | `RCLI7003` |

Each name is reported once, and the message names the new name; a package error also names `rax migrate`. The codes
are in [the diagnostics reference](../../docs/guides/command-line/diagnostics.md#build-targets).
0.15 lists no name; the rename release fills the lists.

## Changing the list

Edit [`old-names.json`](old-names.json), then regenerate and run the tests:

```sh
bun eng/rename/old-names.mjs           # or node
bun test eng/rename/old-names.test.mjs # the guard files match their inputs (CI's workflows job)
dotnet run --project tests/dotnet/Runic.CommandLine.Tests -c Release -- --treenode-filter "/*/*/OldNameGuardsTests/*"
```

`OldNameGuardsTests` runs a guard file generated from a test-only list,
[`tests/dotnet/Runic.CommandLine.Tests/OldNameGuards/old-names.json`](../../tests/dotnet/Runic.CommandLine.Tests/OldNameGuards/old-names.json),
in MSBuild, so the managed suites need no JavaScript runtime. Regenerate it with
`bun eng/rename/old-names.mjs --input tests/dotnet/Runic.CommandLine.Tests/OldNameGuards/old-names.json` after changing
the script.

The input, version 1:

```jsonc
{
  "version": 1,
  "family": {
    "token": "RunicCommandLine",         // prefix of the private items and the guard targets
    "buildName": "Runic Command Line",   // "The Runic Command Line build property X is renamed to Y."
    "helpLinkBase": "$(RunicDiagnosticsHelpLinkBase)", // buildTransitive/Runic.CommandLine.props, at the release tag
    "diagnostics": { "packages": "RCLI7001", "msbuild": "RCLI7002", "environment": "RCLI7003" },
    "skipCondition": "…",                // WPF's temporary markup project reports nothing
    "root": "../..",                     // the repository root, relative to this file; outputs are relative to it
    "outputs": [ "packages/dotnet/Runic.CommandLine/buildTransitive/Runic.CommandLine.RenamedNames.targets" ]
  },
  // The entries take the shape of the rename plan's rax-map.json names: old, new and (msbuild) kind.
  "packages":    [ { "old": "Runic.CommandLine", "new": "RunicArtifex.CommandLine" } ],
  "msbuild":     [ { "old": "RunicCommandLineExample", "new": "RaxCommandLineExample", "kind": "property" },
                   { "old": "RunicCommandLineItem", "new": "RaxCommandLineItem", "kind": "item",
                     "whenNewUnset": false,      // true: stays quiet when the project also sets the new name
                     "note": "…" } ],            // optional: a comment above the item
  "environment": [ { "old": "RUNIC_COMMANDLINE_X", "new": "RAX_COMMANDLINE_X" } ]
}
```

Package IDs compare without case. A family may list the old IDs of another family's packages that its own packages
cannot work beside.

`old-names.mjs` is a copy of runic-sdk's `packages/dotnet/Runic.Application.Views/rename/old-names.mjs`; runic-translations-sdk
has the same copy. Each repository's family settings are in its input, so the copies stay identical: change the script in
runic-sdk first, then copy it here.
