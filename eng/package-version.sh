#!/usr/bin/env bash
set -euo pipefail
if [[ $# -gt 1 ]]; then echo "Usage: $0 [version]" >&2; exit 2; fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=${1:-$(sed -n 's:.*<RunicCommandLineVersion>\(.*\)</RunicCommandLineVersion>.*:\1:p' "$root/eng/Versions.props")}
if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
  echo "Version must be SemVer-compatible (explicit input or eng/Versions.props)." >&2
  exit 2
fi
printf '%s\n' "$version"
