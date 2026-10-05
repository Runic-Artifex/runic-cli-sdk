#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ $# -gt 3 ]]; then echo "Usage: $0 [version] [package-directory] [runtime-identifier]" >&2; exit 2; fi
version=$("$root/eng/package-version.sh" "${1:-}")
output=${2:-"$root/artifacts/packages"}
aot_arguments=()
if [[ -n ${3:-} ]]; then aot_arguments=(-RuntimeIdentifier "$3"); fi
"$root/eng/test.sh"
pwsh -NoProfile -File "$root/tests/native/Runic.CommandLine.AotSmoke/Invoke-AotSmoke.ps1" -Configuration Release "${aot_arguments[@]}"
"$root/eng/pack.sh" "$version" "$output"
"$root/eng/verify-packages.sh" "$version" "$output" "${3:-}"
