# Security policy

## Reporting a vulnerability

Report vulnerabilities privately through
[GitHub private vulnerability reporting](https://github.com/Runic-Artifex/runic-cli-sdk/security/advisories/new)
(**Security** > **Report a vulnerability**). Do not open a public issue, pull
request or discussion for an unfixed vulnerability.

Include the affected package and version, the operating system, whether the
application runs standalone or hosted, reproduction steps, and the impact you
observed. We acknowledge reports as soon as we can, keep you informed while we
investigate, and credit you in the advisory unless you prefer otherwise.

## Supported versions

Runic Command Line is in preview until 1.0, with plain `0.y.z` versions from
0.8.0. Fixes ship in a patch of the latest minor release only; earlier releases
do not receive backported fixes. Upgrade to the
[latest release](https://github.com/Runic-Artifex/runic-cli-sdk/releases) before
reporting.

## Scope

This repository covers the `Runic.CommandLine`, `Runic.CommandLine.Processes`,
`Runic.CommandLine.Spectre` and `Runic.CommandLine.Testing` NuGet packages.
Report issues in the Runic SDK, Runic Translations or the website to their own
repositories:
[runic-sdk](https://github.com/Runic-Artifex/runic-sdk/security),
[runic-translations-sdk](https://github.com/Runic-Artifex/runic-translations-sdk/security) and
[runic-site](https://github.com/Runic-Artifex/runic-site/security).

## Security-relevant behaviour

Runic tools and applications rely on these behaviours of the packages; a way
to bypass one is in scope for a report:

- **Fault sanitizer.** A writer sanitizes every fault before output, human or
  JSON, and diagnostic arguments exclude raw token values unless a command
  marks them safe. The check for technical content is a heuristic, not a
  guarantee; the
  [protocol](specs/command-line/protocol/README.md#diagnostic-object) lists
  what it matches and what producers must keep out of faults themselves.
- **Executable policy.** `Runic.CommandLine.Processes` starts children without
  a shell, evaluates every request with `IExecutablePolicy`, and rejects
  Windows batch files without an opt-in and, with it, arguments that `cmd.exe`
  would interpret (`RCLI6001`–`RCLI6008`); see
  [security and failure behavior](packages/dotnet/Runic.CommandLine.Processes/README.md#security-and-failure-behavior).
- **Environment inheritance.** A child inherits the parent environment unless
  `InheritEnvironment` is false; see
  [standard input and environment](packages/dotnet/Runic.CommandLine.Processes/README.md#standard-input-and-environment).
- **Termination signals.** `CommandApp` cancels the invocation on the first
  termination signal and exits at once on a repeated one unless the command
  holds a shutdown protection scope; see the
  [Runic.CommandLine README](packages/dotnet/Runic.CommandLine/README.md#presentation-and-testing).

## Verifying releases

Every published NuGet package and GitHub release asset has a signed
build-provenance attestation, and each release has a CycloneDX SBOM that is
attached to the release and attested for its packages. Verify a file with
`gh attestation verify <file> -R Runic-Artifex/runic-cli-sdk --signer-workflow Runic-Artifex/runic-cli-sdk/.github/workflows/publish-preview.yml`.
The attestation shows that the publish workflow of this repository, which runs
only on `main`, published those bytes. It does not build them: it takes the
packages from a successful CI run of the same commit and checks them by hash
before attesting them. Packages
downloaded from NuGet.org carry NuGet.org's repository signature and therefore
differ from the attested bytes; verify the copy attached to the GitHub release
instead. See [verifying a release](eng/release/README.md#verifying-a-release).

## Dependency audits

Verification builds fail on NuGet packages with known vulnerabilities, and the
weekly [dependency audit workflow](.github/workflows/dependency-audit.yml) reports
vulnerabilities disclosed between pushes.
