{
  description = "G600 G9-G20 -> F13-F24 software remap daemon";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-parts.url = "github:hercules-ci/flake-parts";
  };

  outputs = inputs@{ self, flake-parts, ... }:
    flake-parts.lib.mkFlake { inherit inputs; } {
      systems = [ "x86_64-linux" "aarch64-linux" ];

      perSystem = { pkgs, system, ... }: {
        packages.default = pkgs.buildDotnetModule {
          pname = "g600-key-remap-daemon";
          version = "0.1.0";
          src = ./.;
          projectFile = "g600-key-remap-daemon.csproj";
          # Zero PackageReferences in the csproj, but buildDotnetModule still wants a
          # lockfile for the implicit SDK/runtime pack restore. Generate it once with:
          #   nix run .#default.fetch-deps -- ./deps.json
          # then commit deps.json alongside this flake.
          nugetDeps = ./deps.json;
          dotnet-sdk = pkgs.dotnet-sdk_9;
          dotnet-runtime = pkgs.dotnet-runtime_9;
        };

        devShells.default = pkgs.mkShell {
          packages = [ pkgs.dotnet-sdk_9 ];
        };
      };

      flake.nixosModules.g600-key-remap = { config, lib, pkgs, ... }:
        let
          cfg = config.services.g600-key-remap;
        in
        {
          options.services.g600-key-remap = {
            enable = lib.mkEnableOption "G600 G9-G20 -> F13-F24 software key remap daemon";
            package = lib.mkOption {
              type = lib.types.package;
              # Referenced via the outer `outputs` closure, not as a module arg — keeps
              # this out of the self/_module.args infinite-recursion trap.
              default = self.packages.${pkgs.system}.default;
              description = "Package providing bin/g600-key-remap-daemon. Defaults to this flake's own build.";
            };
          };

          config = lib.mkIf cfg.enable {
            # Needed for /dev/uinput to exist at all.
            hardware.uinput.enable = true;

            systemd.services.g600-key-remap = {
              description = "Remap G600 G9-G20 to F13-F24 (software, no onboard writes)";
              wantedBy = [ "multi-user.target" ];
              after = [ "systemd-udev-settle.service" ];

              serviceConfig = {
                # Root keeps this simple: it needs to open whichever /dev/input/eventN
                # turns out to be the G600's keyboard interface, plus /dev/uinput.
                ExecStart = "${cfg.package}/bin/g600-key-remap-daemon";
                Restart = "on-failure";
                RestartSec = 2;
              };
            };
          };
        };
    };
}
