# Releasing Runic Command Line

Command Line releases its four NuGet packages together: `Runic.CommandLine`,
`Runic.CommandLine.Processes`, `Runic.CommandLine.Spectre`, and
`Runic.CommandLine.Testing`. The generator is embedded in the core package and
does not have a public package identity.

Set the intended SemVer preview in `eng/Versions.props`, run `./eng/verify.sh`,
then dispatch **Publish preview** on `main` with the same version. The workflow
verifies and packs the source, publishes only packages that do not already exist,
and creates the GitHub prerelease from those exact artifacts.

Before the first standalone publication, register NuGet trusted publishers for
`Runic-Artifex/runic-cli-sdk`, `publish-preview.yml`, and the `preview`
environment. Existing package ownership does not transfer trusted-publisher
registration from the SDK repository.
