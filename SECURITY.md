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

Runic Command Line is in preview. Fixes ship in the next preview release; earlier
previews do not receive backported fixes. Upgrade to the
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

Verification builds fail on NuGet packages with known vulnerabilities, and the
weekly [dependency audit workflow](.github/workflows/dependency-audit.yml) reports
vulnerabilities disclosed between pushes.
