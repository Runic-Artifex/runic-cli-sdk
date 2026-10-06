# Releasing Runic Command Line

Command Line releases its four NuGet packages together: `Runic.CommandLine`,
`Runic.CommandLine.Processes`, `Runic.CommandLine.Spectre`, and
`Runic.CommandLine.Testing`. The generator is embedded in the core package and
does not have a public package identity.

Record behavior changes that need consumer action in `eng/release/notes/<version>.md`.
Set the intended SemVer preview in `eng/Versions.props`, run `./eng/verify.sh`,
then dispatch **Publish preview** on `main` with the same version. The workflow
verifies and packs the source, publishes only packages that do not already exist,
and creates the GitHub prerelease from those exact artifacts.

`eng/Versions.props` is the committed candidate authority; it does not mean the
version exists on NuGet. `./eng/package-version.sh` prints that version.
`./eng/verify.sh <version> [package-directory] [runtime-identifier]` can check an
explicit local candidate without editing the property. Publication still requires
the dispatch version to match the committed property. Command Line versions remain
independent of Runic SDK and Runic Translations versions.
SemVer build metadata is accepted in the input but ignored by NuGet package
identity: `1.2.3-rc.1+build.2` produces and consumes package version `1.2.3-rc.1`
and its corresponding archive names.
It does not create a distinct NuGet release. Dispatch equality still compares
the complete committed and requested version strings.

To verify existing candidate artifacts without rebuilding or packing them:

```sh
./eng/verify-packages.sh "$(./eng/package-version.sh)" /absolute/path/to/candidate-feed
```

This requires all four exact-version `.nupkg` files. The isolated consumer maps
every `Runic.CommandLine*` identity to that feed; NuGet supplies only third-party
dependencies. It checks the package-only tutorial's human, JSON and help commands
and publishes/runs the consumer with NativeAOT. Temporary consumers and caches are
removed when the run ends. The scripts default to the host runtime; pass a third
argument to `verify-packages.sh` to select a compatible host RID.

CI runs managed verification and packing once, then reuses those candidates for
Linux x64, Windows x64 and macOS Arm64 source/consumer NativeAOT checks. The three
native jobs run sequentially. Each target compiles on its own operating system.
The pinned Nix shell supplies Clang and zlib on Linux; CI installs Linux prerequisites,
uses the Windows runner's Visual C++ build tools and macOS's Xcode command-line tools.
For other local environments, follow Microsoft's
[NativeAOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

Before the first standalone publication, register NuGet trusted publishers for
`Runic-Artifex/runic-cli-sdk`, `publish-preview.yml`, and the `preview`
environment. Existing package ownership does not transfer trusted-publisher
registration from the SDK repository.
