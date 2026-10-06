#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 2 ]]; then echo "Usage: $0 <version> <output-directory>" >&2; exit 2; fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=$("$root/eng/package-version.sh" "$1")
package_version=${version%%+*}
packages=(Runic.CommandLine Runic.CommandLine.Processes Runic.CommandLine.Spectre Runic.CommandLine.Testing)
mkdir -p "$(dirname "$2")"
parent="$(cd "$(dirname "$2")" && pwd)"
name="$(basename "$2")"
output="$parent/$name"
# Pack into a sibling staging directory and rename it over the output only after
# every package exists. A failed or interrupted run keeps the previous output;
# the next run removes its leftovers.
rm -rf -- "$parent/.$name-staging-"*
staging="$(mktemp -d "$parent/.$name-staging-XXXXXX")"
trap 'rm -rf -- "$staging"' EXIT
commit="$(git -C "$root" rev-parse HEAD)"
for project in "${packages[@]}"; do
  dotnet pack "$root/packages/dotnet/$project/$project.csproj" -c Release -p:PackageVersion="$version" -p:RepositoryCommit="$commit" -p:RunicCommandLineBuildMode=Verification --output "$staging"
done
for package in "${packages[@]}"; do test -f "$staging/$package.$package_version.nupkg"; done
if [[ -e "$output" ]]; then
  previous="$parent/.$name-previous-$$"
  mv -- "$output" "$previous"
  mv -- "$staging" "$output"
  rm -rf -- "$previous"
else
  mv -- "$staging" "$output"
fi
echo "Packed ${#packages[@]} packages into $output"
