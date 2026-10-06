#!/usr/bin/env bash
set -euo pipefail
# Packs a fresh candidate set and verifies it once with the isolated consumer.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ $# -gt 3 ]]; then echo "Usage: $0 [version] [package-directory] [runtime-identifier]" >&2; exit 2; fi
version=$("$root/eng/package-version.sh" "${1:-}")
output=${2:-"$root/artifacts/packages"}
"$root/eng/pack.sh" "$version" "$output"
"$root/eng/verify-packages.sh" "$version" "$output" "${3:-}"
