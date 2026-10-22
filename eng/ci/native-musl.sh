#!/bin/sh
# The native-aot job's checks for a musl RID, inside the digest-pinned Alpine container
# that .github/workflows/ci.yml starts with `docker run` (GitHub cannot run JavaScript
# actions in an Alpine container on arm64 runners, so the runner checks out and
# downloads the candidate packages, and this script does the rest). It installs the
# NativeAOT toolchain, the .NET SDK pinned in global.json and PowerShell, then runs
# eng/ci/native-aot.ps1. Locally, with rootless Podman, after packing the candidates
# into artifacts/packages:
#
#   podman run --rm --init -v "$PWD:$PWD" -w "$PWD" \
#     <alpine image from ci.yml> sh eng/ci/native-musl.sh linux-musl-x64
#
# Usage: native-musl.sh <linux-musl-x64|linux-musl-arm64>
set -eu
rid=${1:?usage: native-musl.sh <rid>}
case "$rid" in
  linux-musl-x64 | linux-musl-arm64) ;;
  *) echo "native-musl.sh: unknown RID $rid" >&2; exit 2 ;;
esac
# PowerShell runs as a .NET tool; the tool package has no linux-musl-arm64 build, so
# both legs use the same framework-dependent tool on the installed SDK.
powershell_version=7.6.7
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 CI=true
step() { echo "::endgroup::"; echo "::group::$*"; }
retry() { n=0; until "$@"; do n=$((n + 1)); [ "$n" -lt 3 ] || return 1; sleep 10; done; }

echo "::group::Install Alpine packages"
grep PRETTY_NAME /etc/os-release
uname -m
retry apk add --no-cache \
  bash curl jq icu-libs krb5-libs libgcc libintl libstdc++ ncurses-terminfo-base tzdata zlib \
  clang build-base zlib-dev

step "Install .NET"
# The SDK pinned in global.json, verified against Microsoft's release metadata.
sdk=$(jq -r .sdk.version global.json)
file=$(curl -fsSL "https://builds.dotnet.microsoft.com/dotnet/release-metadata/${sdk%%.*}.0/releases.json" \
  | jq -c --arg sdk "$sdk" --arg rid "$rid" '[.releases[].sdks[]? | select(.version == $sdk) | .files[]
      | select(.rid == $rid and (.name | endswith(".tar.gz")))][0]')
test "$file" != null
work=$(mktemp -d)
retry curl -fsSL -o "$work/dotnet.tar.gz" "$(echo "$file" | jq -r .url)"
echo "$(echo "$file" | jq -r .hash)  $work/dotnet.tar.gz" | sha512sum -c -
mkdir -p /usr/share/dotnet
tar -xzf "$work/dotnet.tar.gz" -C /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
rm -rf "$work"
export DOTNET_ROOT=/usr/share/dotnet
dotnet --info | head -20

step "Install PowerShell $powershell_version"
# NuGet verifies the package signature on Linux.
retry dotnet tool install PowerShell --version "$powershell_version" --tool-path /opt/powershell
ln -sf /opt/powershell/pwsh /usr/local/bin/pwsh
pwsh -Version

step "NativeAOT checks for $rid"
pwsh -NoProfile -File eng/ci/native-aot.ps1 -RuntimeIdentifier "$rid"
echo "::endgroup::"
echo "NativeAOT checks passed on $rid."
