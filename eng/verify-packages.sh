#!/usr/bin/env bash
set -euo pipefail
if [[ $# -lt 2 || $# -gt 3 ]]; then echo "Usage: $0 <version> <package-directory> [runtime-identifier]" >&2; exit 2; fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=$("$root/eng/package-version.sh" "$1")
aot_arguments=()
if [[ -n ${3:-} ]]; then aot_arguments=(-RuntimeIdentifier "$3"); fi
pwsh -NoProfile -File "$root/tests/fixtures/command-line/package-consumer/Runic.CommandLine.PackageConsumer/Invoke-PackageConsumer.ps1" -Configuration Release -PackageVersion "$version" -PackageDirectory "$2" "${aot_arguments[@]}"
