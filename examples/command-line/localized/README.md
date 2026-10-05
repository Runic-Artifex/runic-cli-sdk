# Localized CLI

This example uses independently versioned public `Runic.Translations` and
`Runic.Translations.Build` packages at `0.6.0-preview.1`. The core CLI has no
Translations dependency. The CLI source projects provide the candidate APIs
being demonstrated; Translations comes from NuGet, with no sibling source refs.
The Build package generates an immutable catalog from `translations/runic.json`
and the maintained English/German RMF2 sources during compilation.

From the repository's development shell:

```sh
dotnet build examples/command-line/localized/LocalizedCli.csproj -c Release
RCLI_EXAMPLE_CULTURE=en dotnet run --project examples/command-line/localized -c Release --no-build -- greet Ada
RCLI_EXAMPLE_CULTURE=de dotnet run --project examples/command-line/localized -c Release --no-build -- greet Ada
RCLI_EXAMPLE_CULTURE=de dotnet run --project examples/command-line/localized -c Release --no-build -- greet --help
RCLI_EXAMPLE_CULTURE=de dotnet run --project examples/command-line/localized -c Release --no-build -- greet --unknown --output=json
python3 examples/command-line/localized/verify.py --configuration Release
```

The greetings are `Hello Ada` and `Hallo Ada`. The final CLI invocation exits
with usage code `2`, uses German presentation text and retains `RCLI1001`,
`diagnostics.unknown-option`, the canonical `greet` path and `--unknown` argument.
The verification script asserts these contracts in both languages, plus JSON
protocol/payload identity and one-frame output.

`CommandApp.Culture` selects the invocation culture without changing process
culture. The example captures a Translations snapshot before running the CLI,
and its resolver maps CLI text keys to generated Translations keys. The public
Translations preview generates encoded C# member names; the explicit mapping
uses those generated members and never copies catalog ordinals. Some CLI keys
include hyphens, so their corresponding RMF2 resources use camelCase identifiers.
`ICommandTextResolver` receives ordered safe diagnostic arguments; this example's
unknown-option message needs no arguments. For messages that do, map these
ordered values to the resource's named `TextArgument` inputs.

A resolver returns null for an unknown key. Help uses literal `Description`
metadata as fallback and default English framework labels. Description keys are
metadata, never display text. JSON localizes messages and help payload text;
protocol IDs, codes, tokens, paths, payload identities and resource keys stay
stable. A custom help presenter can implement the context overload to honor
localization; existing implementations of the original overload keep working.
