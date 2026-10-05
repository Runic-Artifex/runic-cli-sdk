{
  description = "Runic Command Line development environment";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs = { nixpkgs, ... }:
    let
      supportedSystems = [ "x86_64-linux" "aarch64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
    in {
      devShells = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
          lib = pkgs.lib;
          pinned = pkg: expected: label:
            assert lib.assertMsg (pkg.version == expected)
              "nixpkgs provides ${label} ${pkg.version}, but the repository pins ${expected}";
            pkg;
          dotnet = pinned pkgs.dotnetCorePackages.sdk_10_0 (lib.importJSON ./global.json).sdk.version ".NET SDK";
        in {
          default = pkgs.mkShell {
            packages = with pkgs; [
              git
              curl
              dotnet
              python3
              powershell
              clang
              pkg-config
              zlib
            ];

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_ROOT = "${dotnet}/share/dotnet";
            DisableImplicitLibraryPacksFolder = "true";

            shellHook = ''
              export NUGET_PACKAGES="$PWD/.cache/nuget"
            '';
          };
        });
    };
}
