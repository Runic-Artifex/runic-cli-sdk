#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$root/eng/test.sh"
pwsh -NoProfile -File "$root/tests/native/Runic.CommandLine.AotSmoke/Invoke-AotSmoke.ps1" -Configuration Release -RuntimeIdentifier linux-x64
"$root/eng/pack.sh" 0.6.0-preview.2 "$root/artifacts/packages"
"$root/eng/verify-packages.sh" 0.6.0-preview.2 "$root/artifacts/packages"
