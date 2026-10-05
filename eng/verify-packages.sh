#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 2 ]]; then echo "Usage: $0 <version> <package-directory>" >&2; exit 2; fi
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pwsh -NoProfile -File "$root/tests/fixtures/command-line/package-consumer/Runic.CommandLine.PackageConsumer/Invoke-PackageConsumer.ps1" -Configuration Release -RuntimeIdentifier linux-x64 -PackageVersion "$1" -PackageDirectory "$2"
