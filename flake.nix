{
  description = "wasm2cs pinned self-hosting development environment";

  inputs = {
    # This revision carries .NET SDK 10.0.400, runtime pack 10.0.11,
    # WABT 1.0.41, and the matching wasm-tools workload manifest.
    nixpkgs.url = "github:NixOS/nixpkgs/8ce4ef6cb6f871616146b9fe26d2a5ae594e94fe";

    # Keep the SH-01 Node.js baseline (22.17.0) while the main nixpkgs input
    # advances independently for the pinned .NET/WABT packages.
    nodejs-nixpkgs.url = "github:NixOS/nixpkgs/82976e511069e6006833c5077d8928ec5f8ffc3f";
  };

  outputs = { nixpkgs, nodejs-nixpkgs, ... }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "aarch64-darwin"
      ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
    in {
      devShells = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
          nodePkgs = import nodejs-nixpkgs { inherit system; };
          dotnet = pkgs.dotnetCorePackages.sdk_10_0;
          node = nodePkgs.nodejs_22;
          wabt = pkgs.wabt;
        in {
          default = pkgs.mkShell {
            packages = [ dotnet node wabt ];

            shellHook = ''
              # Do not inherit a user's global SDK state or NuGet cache.
              export DOTNET_ROOT=${dotnet}/share/dotnet
              export DOTNET_ROOT_X64=${dotnet}/share/dotnet
              export DOTNET_CLI_HOME="$PWD/.devshell/dotnet-home"
              export NUGET_PACKAGES="$PWD/.devshell/nuget-packages"
              export MSBuildUserExtensionsPath="$PWD/.devshell/msbuild"
              export DOTNET_NOLOGO=1
            '';
          };
        });

      checks = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
          nodePkgs = import nodejs-nixpkgs { inherit system; };
          dotnet = pkgs.dotnetCorePackages.sdk_10_0;
          node = nodePkgs.nodejs_22;
          wabt = pkgs.wabt;
        in {
          pinned-toolchain = pkgs.runCommand "wasm2cs-pinned-toolchain" {
            nativeBuildInputs = [ dotnet node wabt ];
          } ''
            test "$(${dotnet}/bin/dotnet --version)" = "10.0.400"
            test "$(${dotnet}/bin/dotnet --list-runtimes | ${pkgs.gnugrep}/bin/grep -c '^Microsoft.NETCore.App 10.0.11 ')" -ge 1
            test -d "${dotnet}/share/dotnet/sdk-manifests/10.0.100/microsoft.net.workload.mono.toolchain.current/10.0.111"
            test "$(${node}/bin/node --version)" = "v22.17.0"
            test "$(${wabt}/bin/wasm-objdump --version)" = "1.0.41"
            touch "$out"
          '';
        });
    };
}
