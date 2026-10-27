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
          # global.json disables SDK roll-forward; nix/toolchain provides exactly that version (from the official
          # binaries while nixpkgs is older) and fails evaluation otherwise.
          dotnet = (import ./nix/toolchain { inherit pkgs; }).dotnet-sdk;
        in {
          default = pkgs.mkShell {
            packages = with pkgs; [
              bashInteractive
              git
              curl
              dotnet
              python3
              powershell
              fish
              zsh
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
