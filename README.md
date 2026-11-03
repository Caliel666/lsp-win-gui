# LSP Win GUI

A small Windows x64 desktop utility that checks the latest upstream LSP Audio Plugins source, builds the VST3 + UI targets locally, and installs the resulting bundles.

## What it does

- Checks the latest commit in the official [lsp-plugins/lsp-plugins](https://github.com/lsp-plugins/lsp-plugins) repository.
- Clones that exact commit and fetches the component repositories using the upstream `make fetch` target.
- Configures and compiles the VST3 + UI targets.
- Stages the install and only copies bundles into the selected VST3 folder after a successful build.
- Keeps a live build log in the GUI.

The default install location is `C:\\Program Files\\Common Files\\VST3`. You can select a user-writable directory to avoid elevation.

## Build prerequisites

LSP's official Windows build instructions require MinGW-w64 GCC, GNU Make 4.2+, Git, and PHP 5.5.14+. Install these tools and ensure `git`, `make`, `gcc`, and `php` are available on PATH. The MinGW installation folder can also be selected in the app; its `bin` directory is added to the build PATH.

The upstream project may require additional Windows development libraries depending on the source revision. The app reports compiler/configuration failures in its live log; it does not download or redistribute LSP's paid Windows binaries.

## Build the GUI

Requires the .NET 10 SDK:

```powershell
dotnet restore src/LspWinGui/LspWinGui.csproj
dotnet build src/LspWinGui/LspWinGui.csproj -c Release
dotnet publish src/LspWinGui/LspWinGui.csproj -c Release -r win-x64 --self-contained true
```

GitHub Actions builds and publishes a self-contained Windows x64 executable on pushes to `feature/lsp-vst3-builder` and on pull requests targeting `main`. The workflow artifact is named `LSP-Win-GUI-win-x64`.

## Upstream build procedure

The application follows the upstream source-build sequence:

```sh
make clean
make config FEATURES="vst3 ui"
make fetch
make -j2
make install DESTDIR=<staging-directory>
```

See the [official LSP build instructions](https://lsp-plug.in/?page=manuals&section=building) and upstream source repository for license notices.

## License

This utility is MIT-licensed. LSP Audio Plugins are a separate upstream project; consult their source repository for the license and notices that apply to the plugin source and any resulting build.
