#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 2 ]]; then echo "Usage: $0 <version> <output-directory>" >&2; exit 2; fi
output=$2
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=$("$root/eng/package-version.sh" "$1")
package_version=${version%%+*}
mkdir -p "$output"
for project in Runic.CommandLine Runic.CommandLine.Processes Runic.CommandLine.Spectre Runic.CommandLine.Testing; do
  dotnet pack "$root/packages/dotnet/$project/$project.csproj" -c Release -p:PackageVersion="$version" -p:RepositoryCommit="$(git -C "$root" rev-parse HEAD)" -p:RunicCommandLineBuildMode=Verification --output "$output"
done
for package in Runic.CommandLine Runic.CommandLine.Processes Runic.CommandLine.Spectre Runic.CommandLine.Testing; do test -f "$output/$package.$package_version.nupkg"; done
