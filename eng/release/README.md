# Releasing Runic Command Line

Command Line releases its four NuGet packages together: `Runic.CommandLine`,
`Runic.CommandLine.Processes`, `Runic.CommandLine.Spectre`, and
`Runic.CommandLine.Testing`. The generator is embedded in the core package and
does not have a public package identity.

Record behavior changes that need consumer action in `eng/release/notes/<version>.md`.
Set the intended SemVer preview in `eng/Versions.props`, run `./eng/verify.sh`,
merge to `main` and wait for that commit's CI push run to succeed. Then dispatch
**Publish preview** on `main` with the same version. Check **dry-run** first to run
every check without publishing.

The workflow does not test or pack again. Its `candidate` job (`contents: read`,
`actions: read`, no environment) runs `eng/release/release.py`:

- `find-ci` finds the newest successful `ci.yml` push run on `main` for the
  dispatched commit and its `command-line-packages` artifact. It fails clearly when
  there is no such run, the run is still in progress or failed, or the artifact
  expired (CI keeps it 30 days; rerun all jobs of that CI run to upload it again).
- `actions/download-artifact` downloads it by id (`artifact-ids`, `run-id`,
  `github-token`); a re-upload under the same name gets a new id, so it cannot swap the bytes.
- `prepare` requires exactly the four packages at the requested version, packed from
  the dispatched commit for this repository, and records their hashes in the
  `release-candidate-<run id>` artifact.
- `publish-nuget.py --dry-run` reports which packages are missing on NuGet.org and
  fails if a published version has different contents.
- `release-check` fails if the tag or an existing GitHub release points at another
  commit, or the release is a draft. A release of this exact tag and commit is kept.

A dry run ends there, without OIDC, a NuGet push, a tag or a release. Otherwise the
`publish` job, the only one in the `preview` environment and the only one with
`id-token: write` and `contents: write`, downloads the same artifact, checks it
against the candidate inventory, requested version and CI run (`release.py verify`),
publishes only missing packages and creates the GitHub prerelease at the dispatched
commit from those exact files (`release.py release`). A rerun after a partial
publication keeps an existing release of this tag and commit and uploads only
missing assets. `eng/release/test_release.py` pins this contract and runs in CI.

After publication, update the documentation catalog in `runic-site` (see
"Release catalogs" in its `docs/README.md`). The workflow does not push to other
repositories; the publish summary repeats this reminder.

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
