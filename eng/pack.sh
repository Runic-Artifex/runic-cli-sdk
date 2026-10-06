#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 2 ]]; then echo "Usage: $0 <version> <output-directory>" >&2; exit 2; fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=$("$root/eng/package-version.sh" "$1")
package_version=${version%%+*}
packages=(Runic.CommandLine Runic.CommandLine.Processes Runic.CommandLine.Spectre Runic.CommandLine.Testing)
name="$(basename "$2")"
if [[ "$name" == "." || "$name" == ".." || "$name" == "/" ]]; then
  echo "The output must name a dedicated directory such as artifacts/packages, not '$2'; pack replaces it as a whole." >&2
  exit 2
fi
mkdir -p "$(dirname "$2")"
parent="$(cd "$(dirname "$2")" && pwd)"
output="$parent/$name"
# Pack into a sibling staging directory and rename it over the output only after
# every package exists. A failed or interrupted run keeps the previous output;
# the next run removes its leftovers. Concurrent packs to one output are unsupported.
rm -rf -- "$parent/.$name-staging-"*
for previous in "$parent/.$name-previous-"*; do
  [[ -e "$previous" ]] || continue
  # Only possible when a promotion stopped between its two renames.
  if [[ -e "$output" ]]; then rm -rf -- "$previous"; else mv -- "$previous" "$output"; fi
done
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
  if ! mv -- "$staging" "$output"; then
    mv -- "$previous" "$output"
    echo "Could not promote $staging; kept the previous $output." >&2
    exit 1
  fi
  rm -rf -- "$previous" || echo "warning: could not remove the previous set at $previous" >&2
else
  mv -- "$staging" "$output"
fi
echo "Packed ${#packages[@]} packages into $output"
