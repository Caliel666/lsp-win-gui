# LSP Win GUI

A Windows x64 desktop utility that checks the latest upstream LSP Audio Plugins source, validates the Windows compiler toolchain, builds VST3 + UI targets, and installs the resulting bundles.

## What it does

- Checks the latest commit in the official [lsp-plugins/lsp-plugins](https://github.com/lsp-plugins/lsp-plugins) repository.
- Compares the latest upstream commit with the last successfully installed source commit.
- Saves settings and source-version state in %LOCALAPPDATA%\\LSP-Win-GUI\\settings.json.
- Uses PowerShell Get-Command gcc -All to discover the actual GCC executable instead of assuming a MinGW installation path.
- Verifies that GCC targets x86_64-w64-mingw32, that matching G++ and ld.exe exist beside GCC, and that both C and C++ compile-and-link smoke tests succeed before it modifies the source checkout.
- Rejects Git for Windows usr\\bin as a build-tool directory and places the validated MinGW bin directory first in the build PATH to prevent incompatible linker collisions.
- Clones the exact checked commit, fetches upstream component repositories, stages the install, and only copies VST3 bundles after a successful build.

The default install location is C:\\Program Files\\Common Files\\VST3. You can select a user-writable directory to avoid elevation.

## Build prerequisites

- MinGW-w64 x64 GCC, G++, and matching binutils/linker.
- GNU Make 4.2 or newer (MSYS2 GNU Make is recommended).
- Git for Windows.
- PHP 5.5.14 or newer.

The app discovers commands through PowerShell. If GCC is not on PATH, use the toolchain folder picker to point at the MinGW-w64 root or its bin folder. It will not silently guess C:\\mingw64. A failed compiler/linker smoke test stops the build before source checkout or cleanup.

## Build the GUI

Requires the .NET 10 SDK:

```powershell
dotnet restore src/LspWinGui/LspWinGui.csproj
dotnet build src/LspWinGui/LspWinGui.csproj -c Release
dotnet publish src/LspWinGui/LspWinGui.csproj -c Release -r win-x64 --self-contained true
```

GitHub Actions builds and publishes a self-contained Windows x64 executable on pushes to feature/lsp-vst3-builder and on pull requests targeting main. The workflow artifact is named LSP-Win-GUI-win-x64. When a pull request is merged into main, a separate release workflow increments the app patch version, creates a version tag and GitHub release, generates release notes from the Git commit range since the previous tag, and uploads a Windows x64 ZIP with PDB files removed.

## Upstream build procedure

The application follows the upstream source-build sequence:

```sh
make clean
make config FEATURES=vst3 ADD_FEATURES=ui ARCHITECTURE=x86_64
make fetch
make -j2
make install DESTDIR=<staging-directory>
```

See the [official LSP build instructions](https://lsp-plug.in/?page=manuals&section=building) and upstream source repository for license notices.

## License

This utility is MIT-licensed. LSP Audio Plugins are a separate upstream project; consult their source repository for the license and notices that apply to the plugin source and any resulting build.
