# Releasing Runic Command Line

Command Line releases its four NuGet packages together: `Runic.CommandLine`,
`Runic.CommandLine.Processes`, `Runic.CommandLine.Spectre`, and
`Runic.CommandLine.Testing`. The generator is embedded in the core package and
does not have a public package identity.

Record behavior changes that need consumer action in `eng/release/notes/<version>.md`.
Set the version (`0.y.z` until 1.0) in `eng/Versions.props`, run `./eng/verify.sh`,
merge to `main` and wait for that commit's CI push run to succeed. Then dispatch
**Publish preview** on `main` with the same version. Check **dry-run** first to run
every check without publishing. The workflow keeps the name `publish-preview.yml`
and the `preview` environment for every release, because NuGet trusted publishing
is registered for them; see [versioning](#versioning-and-support-from-10).

The workflow does not test or pack again. Its `candidate` job (`contents: read`,
`actions: read`, no environment) runs `eng/release/release.py`:

- `find-ci` finds the newest successful `ci.yml` push run on `main` for the
  dispatched commit and its `command-line-packages` artifact. It fails clearly when
  there is no such run, the run is still in progress or failed, or the artifact
  expired (CI keeps it 30 days; rerun all jobs of that CI run to upload it again).
- `actions/download-artifact` downloads it by id (`artifact-ids`, `run-id`,
  `github-token`); a re-upload under the same name gets a new id, so it cannot swap the bytes.
- `prepare` requires exactly the four packages at the requested version, packed from
  the dispatched commit for this repository, and records their hashes.
- `describe` writes `runic-cli-sdk-<version>.cdx.json`, a CycloneDX 1.6 SBOM of
  those packages (`sbom.py`, standard library only; it reads package metadata and
  executes nothing). It is deterministic for a commit. The inventory and SBOM are
  uploaded as the `release-candidate-<run id>` artifact, and their `sha256sum`
  lines are passed to `publish` as the job output `release-sha256`.
- `publish-nuget.py --dry-run` reports which packages are missing on NuGet.org and
  fails if a published version has different contents.
- `release-check` fails if the tag (looked up exactly, annotated tags followed) or
  an existing GitHub release points at another commit, or a lookup fails for any
  reason other than not found. A release or draft of this exact commit is kept.

A dry run ends there, without OIDC, an attestation, a NuGet push, a tag or a
release. Otherwise the `publish` job, the only one in the `preview` environment
and the only one with `id-token: write`, `attestations: write` and
`contents: write`, downloads the same artifact and candidate files, checks the
files against `release-sha256` and the packages against the candidate inventory,
requested version and CI run (`release.py verify`). Before anything is published
it attests those verified bytes: `actions/attest-build-provenance` covers the four
packages and the SBOM, and `actions/attest` attaches the SBOM to the packages. It
then publishes only missing packages and creates the GitHub prerelease at the
dispatched commit from those exact packages and the SBOM (`release.py release`). A
rerun after a partial publication keeps an existing release of this tag and
commit; for a draft it uploads only missing assets and publishes it. A published
release is never modified, since immutable releases reject new assets: if it lacks
an asset (for example a release created before the SBOM existed), the step keeps it
unchanged and reports the missing files as a workflow warning instead of failing,
so the rest of the publication can finish. The attestations still cover those files.
`eng/release/test_release.py` and `test_sbom.py` pin this contract and run in CI.

## Verifying a release

Every package and the SBOM of each release have a signed build-provenance
attestation from `publish-preview.yml` on `main`; the packages also have an SBOM
attestation. Verify with the GitHub CLI (`gh auth login` first):

```sh
version=0.8.0
gh release download "v$version" -R Runic-Artifex/runic-cli-sdk
gh attestation verify "Runic.CommandLine.$version.nupkg" -R Runic-Artifex/runic-cli-sdk \
  --signer-workflow Runic-Artifex/runic-cli-sdk/.github/workflows/publish-preview.yml
gh attestation verify "runic-cli-sdk-$version.cdx.json" -R Runic-Artifex/runic-cli-sdk
# The SBOM attestation (CycloneDX); the release asset is the same document.
gh attestation verify "Runic.CommandLine.$version.nupkg" -R Runic-Artifex/runic-cli-sdk \
  --predicate-type https://cyclonedx.org/bom
```

NuGet.org adds its repository signature (`.signature.p7s`) to every package it
accepts, so a `.nupkg` downloaded from NuGet.org has different bytes from the
attested one and does not verify by itself. Verify the copy attached to the GitHub
release; every entry of the NuGet.org package except `.signature.p7s` is identical
to it, and `dotnet nuget verify --all <package>` checks the NuGet.org signature.

## After publication

In a pull request in this repository:

1. Move each package's `PublicAPI.Unshipped.txt` entries into
   `PublicAPI.Shipped.txt`. A `*REMOVED*` entry is not moved: delete it together
   with the Shipped line it names.
2. Move the rows of
   `packages/dotnet/Runic.CommandLine.Generators/AnalyzerReleases.Unshipped.md` into
   `AnalyzerReleases.Shipped.md` under a `## Release` header for the published
   version. A plain version is its own header (`## Release 0.8.0`). Headers must
   be `System.Version` values, so the earlier `-preview.N` releases appear as
   four-component headers (`0.7.0-preview.2` is `## Release 0.7.0.2`; see the note
   at the top of that file); they sort before `0.8.0`.
3. Set `RunicCommandLinePackageValidationBaselineVersion` in `eng/Versions.props`
   to the published version and delete any `CompatibilitySuppressions.xml` files,
   which describe breaks from the old baseline.
4. Set `RunicCommandLineVersion` to the next candidate and update the candidate
   and published versions named in `README.md` and `examples/command-line/README.md`.

Then update the documentation catalog in `runic-site`: set the Command Line
product's `version` and `releaseNotes` in `docs/src/lib/docs-data.ts` (see
"Release catalogs" in its `docs/README.md`). The workflow does not push to other
repositories; the publish summary repeats this reminder. Runic SDK pins Command
Line in its `Directory.Packages.props`; adopting the new version there is a
separate pull request in `runic-sdk`.

## Versions and local verification

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

`./eng/verify-candidate.sh [version] [package-directory] [runtime-identifier]`
packs a fresh candidate set and runs the isolated consumer against it once.
`eng/pack.sh` stages the packages beside the output directory and replaces it only
after all four packages exist; an interrupted pack leaves the previous set.

Links that leave a package name the release tag `v<package version>`, never
`main`; `eng/build/release-links.targets` (kept in sync with the Runic SDK's copy)
builds them. Each packed README is a copy whose `blob/main`, `tree/main` and
relative links of this repository point at the tag, and a remaining main-branch
link to any Runic Artifex repository fails the pack. Nothing else in a README
changes, backslashes included; the package consumer check compares them with the
source. The generator's help links
and catalog validation messages open
[`docs/guides/command-line/diagnostics.md`](../../docs/guides/command-line/diagnostics.md)
at the tag, so every reported `RCLI` code needs an entry there;
`DiagnosticCodeRangeTests` fails when one is missing or stale.

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

## Versioning and support from 1.0

This policy takes effect with 1.0.0. Until then every release is a preview with a
plain `0.y.z` version (from 0.8.0; earlier releases were `0.y.z-preview.N`): a new
minor (`0.9.0`) may contain breaking changes, a patch (`0.8.1`) only fixes or adds,
and only the latest minor gets fixes. Releases are still marked as prereleases on
GitHub, and the workflow keeps the name `publish-preview.yml` and the `preview`
environment, to which NuGet trusted publishing is bound. NuGet treats a plain
`0.y.z` as a stable version, so consumers install it without `--prerelease`, and a
floating `0.*` range picks it up. The
[Runic SDK release guide](https://github.com/Runic-Artifex/runic-sdk/blob/main/eng/release/README.md#versioning-and-support-from-10)
has the shared policy for the three SDKs: SemVer rules and what counts as
breaking, enforcement, package tiers, .NET versions, deprecation and fixes. The
[documentation portal](https://docs.runic-artifex.eu/support/policy/) states the
same policy. For Command Line:

- The four packages share the version in `eng/Versions.props` and are stable from
  1.0.0. Previews of a coming version from 1.0 are `x.y.0-rc.N`, and 1.0.0 itself
  follows `1.0.0-rc.N`. The generator stays embedded in `Runic.CommandLine`. Command Line has no
  experimental APIs or preview packages.
- The path to 1.0 is: 0.8.0 carries every breaking change of the 1.0 API
  review; 0.9.0 only adds (the NativeAOT RIDs below, opt-in `@file` response
  files, a per-command outcome presenter and a strict
  `CommandTestEnvelope.Read<T>`); then `1.0.0-rc.N` takes fixes only, and 1.0.0
  follows the last release candidate.
- Command Line depends on no other Runic SDK and ships first in the 1.0 release
  train, before Runic Translations and Runic SDK. After 1.0 it releases minors
  and patches independently; Runic Translations and the Runic SDK tools reference
  it by NuGet minimum version.
- Contracts covered by the breaking-change rules, besides the C# API of each
  package's `PublicAPI.Shipped.txt`: the `RCLI` diagnostic IDs (see
  [`AnalyzerReleases.Shipped.md`](../../packages/dotnet/Runic.CommandLine.Generators/AnalyzerReleases.Shipped.md)),
  the `runic.commandline/1` JSON envelope, and the parsing rules and exit codes
  that Command Line gives an application. Human-readable output and help text
  are not a contract. Spectre.Console types appear in the public
  API of `Runic.CommandLine.Spectre`; moving to a Spectre.Console version that
  changes them is breaking. Spectre.Console is itself pre-1.0, so the dependency
  is a range that ends before its next minor (`[0.57.2, 0.58)` in
  `Directory.Packages.props`). The range moves only in a Command Line minor, after
  the suites and the package consumer pass on the new Spectre.Console minor; an
  application that needs a newer Spectre.Console earlier gets NuGet warning
  `NU1608` and runs untested.
- Package validation runs against `RunicCommandLinePackageValidationBaselineVersion`
  in `eng/Versions.props`; from 1.0 that is the latest stable release of the
  major line, and a `CompatibilitySuppressions.xml` or a `*REMOVED*` entry is
  allowed only as the shared policy describes.
- The core package is managed and runs wherever .NET 10 does. Process and
  terminal code is platform-specific, so NativeAOT applications are supported on
  the RIDs that CI publishes and runs: today `linux-x64`, `win-x64` and
  `osx-arm64`. `linux-arm64`, `win-arm64`, `linux-musl-x64` and
  `linux-musl-arm64` are planned for 0.9.0, because the Runic SDK tools that
  bundle Command Line already run on them.
- Generated-code support API: `GeneratedCommandBinding` and
  `GeneratedCommandBindingException` in `Runic.CommandLine.Generated` are public
  only so that generated code can call them. They are covered for code
  generated by the generator of the same major version, are hidden from
  IntelliSense (`[EditorBrowsable(Never)]`), and are not for hand-written calls,
  which get no compatibility guarantee. The generated `GeneratedCommandCatalog` is `internal` to each assembly and not part
  of the package API.
- Bug and security fixes ship in a patch of the latest minor only. `[Obsolete]`
  APIs stay for at least one minor release and are removed only in a major.

API rules that Command Line keeps, with the reason where it differs from a
common .NET convention:

- Every public enum and string set states whether it is closed (consumers may
  switch over it exhaustively; a new value is breaking) or open (consumers must
  handle unknown values; adding one is not breaking). The statement is part of
  its XML documentation.
- Constructors and positional records are frozen: adding a constructor
  parameter is binary-breaking, so a type such as `CommandHelp`,
  `CommandDiagnostic`, `CommandFault`, `ParseSettings`,
  `ProcessExecutionOptions`, `ProcessRequest` or `CommandAppTestResult` grows
  only through `init` properties.
- The parser and execution interfaces stay public, because the command model is
  parser-neutral and the Testing package, Runic Translations and applications
  implement or construct them. A public interface grows only through default
  interface members.
- Observation uses Command Line's own interfaces (`ICommandExecutionObserver`,
  `ExceptionObserver`, `IProcessObserver`) instead of `ILogger` or
  `ActivitySource`, so the NativeAOT kernel needs no Microsoft.Extensions
  dependency; an application bridges them to its logger.
- `CommandFailureData.TryGet` returns false only for an unknown identity; a
  known identity whose data does not match its shape throws
  `CommandProtocolException`, because that is a protocol violation.
- Command Line has no experimental APIs; a new API is either supported or not
  public.
- An `int` command result is payload data, not the exit code: exit codes come
  from `CommandExitCategory` and the exit-code policy.
- The generator accepts static command methods only, with services injected
  through `[FromServices]`, so binding stays reflection-free and NativeAOT-safe
  without activation rules.
- `ProcessResult.EnsureCompleted` checks that the child ran to completion with
  complete captures, not its exit code; the name follows
  `HttpResponseMessage.EnsureSuccessStatusCode` rather than Runic SDK's
  `ThrowIfFailed`, which would suggest an exit-code check. The public
  `ProcessResult` factories exist for test doubles, and the Processes README
  states the fault each state carries.
- `SpectreCommandConsole.WithProgressAsync` returns `Task`, like Spectre's
  progress API; progress is not a hot path, so it does not use `ValueTask`.
