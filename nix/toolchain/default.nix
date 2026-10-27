# The repository's pinned .NET SDK for the development shell (global.json disables SDK roll-forward).
#
# The locked nixpkgs still has .NET SDK 10.0.401 (runtime 10.0.12), older than the October 2026 security release the
# repository pins (D050). Until it catches up, the SDK comes from the official binaries through nixpkgs' own binary SDK
# builder (`dotnetCorePackages.buildDotnetSdk`, the `sdk_10_0-bin` machinery, so `.packages`, `.runtime`,
# `.aspnetcore` and `share/dotnet` behave as for nixpkgs' SDK) with ./dotnet-10.0.nix, copied from runic-sdk.
# Evaluation warns once nixpkgs provides the pin; then delete this directory and use
# `pkgs.dotnetCorePackages.sdk_10_0` again.
{ pkgs }:
let
  inherit (pkgs) lib;
  dotnetPin = (lib.importJSON ../../global.json).sdk.version;
  # Fail at evaluation when the provided version differs from the repository pin.
  pinned =
    pkg: expected: label:
    assert lib.assertMsg (
      pkg.version == expected
    ) "The Runic Command Line toolchain provides ${label} ${pkg.version}, but the repository pins ${expected}";
    pkg;
  untilNixpkgsHas =
    nixpkgsPkg: expected: label:
    lib.warnIf (lib.versionAtLeast nixpkgsPkg.version expected)
      "nixpkgs now provides ${label} ${nixpkgsPkg.version}; remove the official-binary override in nix/toolchain";
in
{
  dotnet-sdk = pinned (untilNixpkgsHas pkgs.dotnetCorePackages.sdk_10_0 dotnetPin ".NET SDK"
    (pkgs.dotnetCorePackages.buildDotnetSdk ./dotnet-10.0.nix).sdk_10_0
  ) dotnetPin ".NET SDK";
}
