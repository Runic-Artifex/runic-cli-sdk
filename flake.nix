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
          # Bun from the official release archive, for the conformance driver (eng/conformance); the same pin and
          # hashes as runic-sdk and runic-translations-sdk, which match the release's SHASUMS256.txt.
          bunArchive =
            if system == "x86_64-linux" then
              {
                platform = "linux-x64";
                hash = "sha256-SQxqWbkwuEx42tLBeD8BdpDH1bxUasS1yOkkDULCz3U=";
              }
            else
              {
                platform = "linux-aarch64";
                hash = "sha256-NhWc7T3/G+CA3TE/q/vQR5oOPfBLPgZ6Exdqa1CjD5w=";
              };
          bun_1_4_4 = pkgs.stdenvNoCC.mkDerivation {
            pname = "bun";
            version = "1.4.4";
            src = pkgs.fetchzip {
              url = "https://github.com/oven-sh/bun/releases/download/bun-v1.4.4/bun-${bunArchive.platform}.zip";
              inherit (bunArchive) hash;
            };
            nativeBuildInputs = [ pkgs.autoPatchelfHook ];
            buildInputs = [ pkgs.stdenv.cc.cc.lib ];
            dontBuild = true;
            installPhase = ''
              mkdir -p "$out/bin"
              cp bun "$out/bin/bun"
              chmod +x "$out/bin/bun"
            '';
          };
        in {
          default = pkgs.mkShell {
            packages = with pkgs; [
              bashInteractive
              git
              curl
              dotnet
              bun_1_4_4
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
