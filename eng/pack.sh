#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 2 ]]; then echo "Usage: $0 <version> <output-directory>" >&2; exit 2; fi
version=$1
output=$2
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]]; then echo "Version must be SemVer-compatible." >&2; exit 2; fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$output"
for project in Runic.CommandLine Runic.CommandLine.Processes Runic.CommandLine.Spectre Runic.CommandLine.Testing; do
  dotnet pack "$root/packages/dotnet/$project/$project.csproj" -c Release -p:PackageVersion="$version" -p:RepositoryCommit="$(git -C "$root" rev-parse HEAD)" -p:RunicCommandLineBuildMode=Verification --output "$output"
done
for package in Runic.CommandLine Runic.CommandLine.Processes Runic.CommandLine.Spectre Runic.CommandLine.Testing; do test -f "$output/$package.$version.nupkg"; done
