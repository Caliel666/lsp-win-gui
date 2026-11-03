using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LspWinGui.Services;

public sealed record UpstreamSource(string Sha, DateTime Date);
public sealed record BuildResult(int BundleCount, string InstallPath);
public sealed record BuildProgressUpdate(double Percent, string Status, string Detail, bool IsIndeterminate = false);
public sealed record ToolchainInfo(string Gcc, string Gxx, string Linker, string Make, string Git, string Php, string CompilerBin, string MakeBin, string Target);

public sealed class LspBuildService
{
    private const string Repository = "https://github.com/lsp-plugins/lsp-plugins.git";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LSP-Win-GUI", "0.2.0"));
        return client;
    }

    public async Task<UpstreamSource> GetLatestSourceAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync("https://api.github.com/repos/lsp-plugins/lsp-plugins/commits/master", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = json.RootElement;
        var sha = root.GetProperty("sha").GetString() ?? throw new InvalidOperationException("GitHub did not return a commit SHA.");
        var date = root.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTime();
        return new UpstreamSource(sha, date);
    }

    public async Task<ToolchainInfo> ValidateToolchainAsync(string selectedRoot, IProgress<string> log, CancellationToken cancellationToken = default)
    {
        log.Report("Checking build tools using PowerShell Get-Command (not guessed install paths)…");
        var gccPaths = await GetCommandPathsAsync("gcc", log, cancellationToken);
        var makePaths = await GetCommandPathsAsync("make", log, cancellationToken);
        var gitPaths = await GetCommandPathsAsync("git", log, cancellationToken);
        var phpPaths = await GetCommandPathsAsync("php", log, cancellationToken);

        if (!string.IsNullOrWhiteSpace(selectedRoot))
        {
            var selected = Path.GetFullPath(selectedRoot.Trim().Trim('"'));
            var selectedBin = Path.GetFileName(selected).Equals("bin", StringComparison.OrdinalIgnoreCase) ? selected : Path.Combine(selected, "bin");
            var selectedGcc = Path.Combine(selectedBin, "gcc.exe");
            if (File.Exists(selectedGcc)) gccPaths.Insert(0, selectedGcc);
        }

        var git = gitPaths.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Git was not found. Install Git for Windows and make sure Get-Command git resolves it.");
        var php = phpPaths.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("PHP was not found. Install PHP 5.5.14 or newer and make sure Get-Command php resolves it.");
        var gitVersion = await CaptureAsync(git, "--version", null, cancellationToken);
        var phpVersion = await CaptureAsync(php, "-v", null, cancellationToken);
        if (gitVersion.ExitCode != 0) throw new InvalidOperationException($"Git was found at {git}, but could not run: {LastLines(gitVersion.Output, 4)}");
        if (phpVersion.ExitCode != 0 || !MeetsMinimumVersion(phpVersion.Output, new Version(5, 5, 14)))
            throw new InvalidOperationException($"PHP 5.5.14 or newer is required. Found: {FirstLine(phpVersion.Output)}");

        string? make = null;
        foreach (var candidate in makePaths.Where(File.Exists))
        {
            var dir = Path.GetDirectoryName(candidate)!;
            if (IsGitUsrBin(dir)) continue;
            var version = await CaptureAsync(candidate, "--version", null, cancellationToken);
            if (version.ExitCode == 0 && version.Output.Contains("GNU Make", StringComparison.OrdinalIgnoreCase) &&
                MeetsMinimumVersion(version.Output, new Version(4, 2)))
            {
                make = candidate;
                break;
            }
        }
        if (make is null)
            throw new InvalidOperationException("GNU Make was not found. Install GNU Make from MSYS2 (for example, its usr\\bin\\make.exe) and ensure Get-Command make resolves it. Git for Windows usr\\bin is kept for shell utilities but is ordered after MinGW.");

        ToolchainInfo? selectedToolchain = null;
        var failures = new List<string>();
        foreach (var gcc in gccPaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bin = Path.GetDirectoryName(gcc)!;
            var gxx = Path.Combine(bin, "g++.exe");
            var linker = Path.Combine(bin, "ld.exe");
            if (!File.Exists(gxx) || !File.Exists(linker))
            {
                failures.Add($"{gcc}: missing matching g++.exe or ld.exe beside gcc.exe");
                continue;
            }

            var target = await CaptureAsync(gcc, "-dumpmachine", null, cancellationToken);
            if (target.ExitCode != 0 || !target.Output.Trim().StartsWith("x86_64-w64-mingw32", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{gcc}: expected x86_64-w64-mingw32, got '{target.Output.Trim()}'");
                continue;
            }

            var gccVersion = await CaptureAsync(gcc, "--version", null, cancellationToken);
            if (gccVersion.ExitCode != 0 || !MeetsMinimumVersion(gccVersion.Output, new Version(7, 0)))
            {
                failures.Add($"{gcc}: MinGW-w64 GCC 7.0 or newer is required. Found: {FirstLine(gccVersion.Output)}");
                continue;
            }

            var candidateLinkerVersion = await CaptureAsync(linker, "-V", null, cancellationToken);
            var gxxVersion = await CaptureAsync(gxx, "--version", null, cancellationToken);
            if (candidateLinkerVersion.ExitCode != 0 || gxxVersion.ExitCode != 0)
            {
                failures.Add($"{gcc}: matching linker or C++ compiler could not run");
                continue;
            }

            var testDir = Path.Combine(Path.GetTempPath(), "LSP-Win-GUI-toolcheck-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            try
            {
                var source = Path.Combine(testDir, "toolchain-test.c");
                var output = Path.Combine(testDir, "toolchain-test.exe");
                await File.WriteAllTextAsync(source, "int main(void) { return 0; }", cancellationToken);
                var env = CreateBuildEnvironment(bin, Path.GetDirectoryName(make)!, Path.GetDirectoryName(php)!, null, git);

                // An x86_64-w64-mingw32 triple alone is not enough: verify the
                // compiler actually defines the Windows x64 ABI macros expected
                // by LSP's Windows-specific code paths.
                var macros = await CaptureAsync(gcc, $"-dM -E \"{source}\"", env, cancellationToken);
                if (macros.ExitCode != 0 ||
                    !Regex.IsMatch(macros.Output, @"(?m)^#define _WIN32(?:\s|$)") ||
                    !Regex.IsMatch(macros.Output, @"(?m)^#define _WIN64(?:\s|$)"))
                {
                    failures.Add($"{gcc}: compiler does not expose both _WIN32 and _WIN64 macros. {LastLines(macros.Output, 8)}");
                    continue;
                }

                var compile = await CaptureAsync(gcc, $"-v \"{source}\" -o \"{output}\"", env, cancellationToken);
                if (compile.ExitCode != 0 || !File.Exists(output))
                {
                    failures.Add($"{gcc}: test link failed. {LastLines(compile.Output, 8)}");
                    continue;
                }

                var cppSource = Path.Combine(testDir, "toolchain-test.cpp");
                var cppOutput = Path.Combine(testDir, "toolchain-test-cpp.exe");
                await File.WriteAllTextAsync(cppSource, "int main() { return 0; }", cancellationToken);
                var cppCompile = await CaptureAsync(gxx, $"\"{cppSource}\" -o \"{cppOutput}\"", env, cancellationToken);
                if (cppCompile.ExitCode != 0 || !File.Exists(cppOutput))
                {
                    failures.Add($"{gxx}: C++ test link failed. {LastLines(cppCompile.Output, 8)}");
                    continue;
                }

                selectedToolchain = new ToolchainInfo(gcc, gxx, linker, make, git, php, bin, Path.GetDirectoryName(make)!, target.Output.Trim());
                break;
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        if (selectedToolchain is null)
            throw new InvalidOperationException("No compatible 64-bit MinGW-w64 toolchain passed the compile-and-link test.\n" + string.Join("\n", failures) +
                "\n\nInstall a matching MinGW-w64 GCC + G++ + binutils toolchain. The selected gcc.exe, g++.exe and ld.exe must come from the same bin directory.");

        var makeVersion = await CaptureAsync(selectedToolchain.Make, "--version", null, cancellationToken);
        var linkerVersion = await CaptureAsync(selectedToolchain.Linker, "-V", null, cancellationToken);
        log.Report($"PowerShell (Get-Command gcc).Source resolved: {selectedToolchain.Gcc}");
        log.Report($"Compiler target: {selectedToolchain.Target}");
        log.Report($"Matching C++ compiler: {selectedToolchain.Gxx}");
        log.Report($"Matching linker: {selectedToolchain.Linker}");
        log.Report($"GNU Make: {selectedToolchain.Make} — {FirstLine(makeVersion.Output)}");
        log.Report($"Git: {selectedToolchain.Git}");
        log.Report($"PHP: {selectedToolchain.Php} — {FirstLine(phpVersion.Output)}");
        log.Report($"Linker emulations: {LastLines(linkerVersion.Output, 4)}");
        log.Report("C and C++ compile-and-link smoke tests passed. Toolchain is ready.");
        return selectedToolchain;
    }

    public async Task<BuildResult> BuildAndInstallAsync(string commit, string toolchainRoot, string installPath, IProgress<string> log, CancellationToken cancellationToken = default, IProgress<BuildProgressUpdate>? buildProgress = null)
    {
        var tools = await ValidateToolchainAsync(toolchainRoot, log, cancellationToken);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LSP-Win-GUI");
        var sourceDir = Path.Combine(root, "source");
        Directory.CreateDirectory(root);
        buildProgress?.Report(new BuildProgressUpdate(2, "Preparing source checkout…", "Checking whether the existing source and build cache can be reused."));
        var existingRepo = (ExitCode: 1, Output: string.Empty);
        if (Directory.Exists(Path.Combine(sourceDir, ".git")))
            existingRepo = await CaptureAsync(tools.Git, "rev-parse --is-inside-work-tree", null, cancellationToken, sourceDir);
        if (existingRepo.ExitCode == 0 && existingRepo.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            var current = await CaptureAsync(tools.Git, "rev-parse HEAD", null, cancellationToken, sourceDir);
            if (current.ExitCode != 0)
                throw new InvalidOperationException("The existing source checkout is present but Git could not read its current commit. It was preserved; inspect the checkout before retrying.");
            if (current.Output.Trim().Equals(commit, StringComparison.OrdinalIgnoreCase))
                log.Report($"Reusing source checkout at {commit}; preserving fetched dependencies and incremental .build outputs.");
            else
            {
                log.Report($"Updating existing checkout from {current.Output.Trim()} to {commit}; preserving the build cache and dependency repositories.");
                await RunAsync(tools.Git, "reset --hard", sourceDir, null, log, cancellationToken);
                await RunAsync(tools.Git, $"fetch --filter=blob:none origin {commit}", sourceDir, null, log, cancellationToken);
                await RunAsync(tools.Git, $"checkout --detach {commit}", sourceDir, null, log, cancellationToken);
            }
        }
        else
        {
            if (Directory.Exists(sourceDir))
            {
                log.Report("The saved source folder is not a valid Git checkout. Replacing only this invalid checkout.");
                await DeleteDirectorySafelyAsync(sourceDir, cancellationToken);
            }
            log.Report($"Cloning official source at {commit}…");
            await RunAsync(tools.Git, $"clone --filter=blob:none {Repository} \"{sourceDir}\"", root, null, log, cancellationToken);
            await RunAsync(tools.Git, $"checkout --detach {commit}", sourceDir, null, log, cancellationToken);
        }
        var actual = await CaptureAsync(tools.Git, "rev-parse HEAD", null, cancellationToken, sourceDir);
        if (actual.ExitCode != 0 || !actual.Output.Trim().Equals(commit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Source checkout mismatch. Requested {commit}, got {actual.Output.Trim()}.");

        var env = CreateBuildEnvironment(tools.CompilerBin, tools.MakeBin, Path.GetDirectoryName(tools.Php)!, log, tools.Git);
        var buildOutputDir = Path.Combine(sourceDir, ".build");
        if (Directory.Exists(buildOutputDir))
            log.Report("Keeping existing .build outputs for incremental compilation; failed builds can resume without rebuilding completed objects.");
        else
            log.Report("No existing .build cache found; this is the first compilation for this checkout.");
        buildProgress?.Report(new BuildProgressUpdate(8, "Configuring x64 VST3 + UI…", "Existing object files and fetched component repositories will be retained."));
        log.Report("Configuring x64 VST3 + UI targets…");
        // Pass each feature as its own make variable. A space-containing FEATURES
        // assignment is re-emitted through upstream MAKEFLAGS and can be split
        // into an unintended make target on Windows.
        // The upstream makefiles derive HOST_BUILD_ARCH from PROCESSOR_ARCHITECTURE.
        // Force it explicitly: on some native Windows/MinGW combinations the process
        // reports i686 even though the selected compiler and plugin target are x64.
        // Without this, host-side tools are incorrectly compiled with -m32.
        // Do not override PREFIX on Windows. Upstream make/paths.mk deliberately
        // defaults it to $(BASEDIR)/INSTALL for Windows. Passing the POSIX default
        // /usr/local makes Git for Windows' MSYS shell rewrite it to
        // "C:/Program Files/Git/usr/local"; the resulting space-containing path
        // breaks the quoted LSP_INSTALL_PREFIX compiler definition.
        var configOutput = await RunAsync(tools.Make,
            "config FEATURES=vst3 ADD_FEATURES=ui ARCHITECTURE=x86_64 HOST_BUILD_ARCH=x86_64",
            sourceDir, env, log, cancellationToken);

        // .config.mk is an exported variable cache, not a stable human-readable
        // summary. Validate the authoritative summary emitted by make/configure.mk.
        // It prints the resolved target architecture and sorted BUILD_FEATURES list.
        var configPath = Path.Combine(sourceDir, ".config.mk");
        var configLines = File.Exists(configPath) ? await File.ReadAllLinesAsync(configPath, cancellationToken) : Array.Empty<string>();
        var prefixLine = configLines.FirstOrDefault(line => line.StartsWith("PREFIX=", StringComparison.Ordinal));
        var configuredPrefix = prefixLine is null ? null : prefixLine["PREFIX=".Length..].Trim().Trim('"');
        var targetMatch = Regex.Match(configOutput, @"(?im)^\s*Architecture:\s*([^\r\n]+)");
        var featuresMatch = Regex.Match(configOutput, @"(?im)^\s*Features:\s*([^\r\n]+)");
        var configuredFeatures = featuresMatch.Success
            ? Regex.Split(featuresMatch.Groups[1].Value.Trim(), @"\s+")
            : Array.Empty<string>();
        if (!configOutput.Contains("Configured OK", StringComparison.OrdinalIgnoreCase) ||
            !targetMatch.Success ||
            !targetMatch.Groups[1].Value.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
            !configuredFeatures.Contains("ui", StringComparer.OrdinalIgnoreCase) ||
            !configuredFeatures.Contains("vst3", StringComparer.OrdinalIgnoreCase))
        {
            var configSummary = configLines
                .Where(line => line.StartsWith("ARCHITECTURE=", StringComparison.Ordinal) ||
                               line.StartsWith("BUILD_FEATURES=", StringComparison.Ordinal) ||
                               line.StartsWith("HOST_ARCHITECTURE=", StringComparison.Ordinal) ||
                               line.StartsWith("PREFIX=", StringComparison.Ordinal));
            throw new InvalidOperationException(
                "Upstream configuration did not confirm x86_64 + UI + VST3. " +
                "Configuration output:\n" + LastLines(configOutput, 20) +
                "\nRelevant .config.mk values:\n" + string.Join(Environment.NewLine, configSummary));
        }
        if (string.IsNullOrWhiteSpace(configuredPrefix))
            throw new InvalidOperationException("Upstream did not write PREFIX to .config.mk; refusing to build with an unknown installation prefix.");
        log.Report("Verified plugin target: " + targetMatch.Groups[1].Value.Trim() +
                   "; configured features: " + string.Join(" ", configuredFeatures) +
                   "; Windows install prefix: " + configuredPrefix);

        // On Windows the upstream PREFIX is already absolute (source\\INSTALL).
        // DESTDIR is a Unix-style prefix concatenation mechanism; combining it
        // with this drive-qualified PREFIX creates an invalid path such as
        // stagingC:/.../source/INSTALL and makes vst_modinfo reject moduleinfo.json.
        var sourceRoot = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var installRoot = Path.GetFullPath(configuredPrefix.Replace('/', Path.DirectorySeparatorChar));
        if (!installRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Upstream Windows install prefix '{installRoot}' is outside the source checkout. Refusing to clean or write to that location.");
        if (Directory.Exists(installRoot))
        {
            log.Report($"Removing previous upstream install output at {installRoot}; the source checkout and .build cache remain untouched.");
            await DeleteDirectorySafelyAsync(installRoot, cancellationToken);
        }
        Directory.CreateDirectory(installRoot);
        buildProgress?.Report(new BuildProgressUpdate(15, "Fetching LSP components…", "Updating only dependencies required by this upstream revision."));
        log.Report("Fetching upstream component repositories…");
        await RunAsync(tools.Make, "fetch", sourceDir, env, log, cancellationToken);
        var modulesDir = Path.Combine(sourceDir, "modules");
        var pluginModules = Directory.Exists(modulesDir)
            ? Directory.GetDirectories(modulesDir, "lsp-plugins-*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return !name.Equals("lsp-plugins-shared", StringComparison.OrdinalIgnoreCase) &&
                           !name.Equals("lsp-plugins-ab-tester", StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(Path.Combine(path, "src"));
                })
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalPlugins = pluginModules.Count;
        var completedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activePlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPlugin = "Preparing plugin build";
        var jobs = Math.Max(1, Environment.ProcessorCount);
        log.Report($"Compiling with {jobs} parallel jobs ({Environment.ProcessorCount} logical processors detected).");
        log.Report(totalPlugins > 0
            ? $"Discovered {totalPlugins} LSP plugin modules for progress estimation."
            : "Plugin module count could not be determined; progress will follow build stages and observed targets.");
        buildProgress?.Report(new BuildProgressUpdate(25, "Compiling LSP plugins…",
            $"Waiting for compiler output · 0/{totalPlugins} plugin modules complete · {jobs} parallel jobs"));
        log.Report("Compiling LSP plugins. This may take a while…");

        void ObserveBuildLine(string line)
        {
            var moduleMatch = Regex.Match(line, @"\[(lsp-plugins-[a-z0-9][a-z0-9-]*)\]", RegexOptions.IgnoreCase);
            if (moduleMatch.Success && pluginModules.Contains(moduleMatch.Groups[1].Value))
            {
                currentPlugin = moduleMatch.Groups[1].Value["lsp-plugins-".Length..].Replace('-', ' ');
                activePlugins.Add(moduleMatch.Groups[1].Value);
            }

            if (line.Contains("Leaving directory", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var module in pluginModules)
                {
                    var native = Path.Combine("modules", module).Replace('\\', '/');
                    if (line.Replace('\\', '/').Contains(native, StringComparison.OrdinalIgnoreCase))
                    {
                        completedPlugins.Add(module);
                        activePlugins.Remove(module);
                    }
                }
            }

            if (moduleMatch.Success && Regex.IsMatch(line, @"^\s*ld\s+\[", RegexOptions.IgnoreCase))
            {
                var module = moduleMatch.Groups[1].Value;
                if (pluginModules.Contains(module))
                {
                    completedPlugins.Add(module);
                    activePlugins.Remove(module);
                }
            }

            var ratio = totalPlugins > 0 ? (double)completedPlugins.Count / totalPlugins : 0;
            var percent = totalPlugins > 0 ? 25 + (60 * ratio) : 25;
            var detail = totalPlugins > 0
                ? $"Building: {currentPlugin} · {completedPlugins.Count}/{totalPlugins} plugin modules complete · {activePlugins.Count} active · {jobs} parallel jobs"
                : $"Building: {currentPlugin} · {jobs} parallel jobs";
            buildProgress?.Report(new BuildProgressUpdate(percent, "Compiling LSP plugins…", detail));
        }

        await RunAsync(tools.Make, $"-j{jobs} VERBOSE=1", sourceDir, env, log, cancellationToken, ObserveBuildLine);
        buildProgress?.Report(new BuildProgressUpdate(86, "Compilation complete — staging VST3…",
            $"All build targets completed · {totalPlugins} plugin modules discovered · preparing bundle metadata."));
        log.Report("Staging installation…");
        buildProgress?.Report(new BuildProgressUpdate(90, "Installing VST3 bundle metadata…", "Generating moduleinfo.json directly under the upstream Windows INSTALL prefix."));
        log.Report($"Running upstream install without DESTDIR; Windows PREFIX is already absolute: {installRoot}");
        await RunAsync(tools.Make, "install", sourceDir, env, log, cancellationToken);

        var bundles = Directory.GetDirectories(installRoot, "*.vst3", SearchOption.AllDirectories);
        if (bundles.Length == 0)
            throw new InvalidOperationException("The build completed but no .vst3 bundles were found in the staged install. Nothing was installed.");
        buildProgress?.Report(new BuildProgressUpdate(95, "Installing VST3 bundles…",
            $"Staged {bundles.Length} bundle(s) · copying them into {installPath}"));

        Directory.CreateDirectory(installPath);
        var backup = Path.Combine(root, "backup");
        if (Directory.Exists(backup)) await DeleteDirectorySafelyAsync(backup, cancellationToken);
        Directory.CreateDirectory(backup);
        var installed = new List<string>();
        try
        {
            foreach (var source in bundles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(source);
                var destination = Path.Combine(installPath, name);
                if (Directory.Exists(destination)) Directory.Move(destination, Path.Combine(backup, name));
                installed.Add(destination);
                CopyDirectory(source, destination);
            }
        }
        catch
        {
            foreach (var path in installed)
                if (Directory.Exists(path)) Directory.Delete(path, true);
            foreach (var old in Directory.GetDirectories(backup))
            {
                var destination = Path.Combine(installPath, Path.GetFileName(old));
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
                Directory.Move(old, destination);
            }
            throw;
        }

        buildProgress?.Report(new BuildProgressUpdate(100, "Build and installation complete.",
            $"Installed {bundles.Length} VST3 bundle(s) · {installPath}"));
        log.Report($"Build complete. Installed {bundles.Length} bundle(s).");
        return new BuildResult(bundles.Length, installPath);
    }

    private static Dictionary<string, string?> CreateBuildEnvironment(
        string compilerBin, string makeBin, string phpBin, IProgress<string>? log, string? gitExecutable = null)
    {
        var inherited = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('"'))
            .Where(p => !string.IsNullOrWhiteSpace(p));

        // Git for Windows usr\\bin provides sh.exe and utilities (cp, install, sed,
        // rm, etc.) required by the upstream GNU Make recipes. Keep it on PATH,
        // but AFTER MinGW so gcc's matching ld.exe/as.exe/ar.exe always take priority.
        var gitUsrBin = string.IsNullOrWhiteSpace(gitExecutable)
            ? null
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitExecutable)!, "..", "usr", "bin"));
        var paths = new[] { compilerBin, makeBin, phpBin }
            .Concat(gitUsrBin is not null && Directory.Exists(gitUsrBin) ? new[] { gitUsrBin } : Array.Empty<string>())
            .Concat(inherited)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string?>
        {
            ["PATH"] = string.Join(Path.PathSeparator, paths),
            ["MSYSTEM"] = null,
            ["PROCESSOR_ARCHITECTURE"] = "AMD64",
            ["CC"] = null,
            ["CXX"] = null,
            ["LD"] = null
        };
        log?.Report("Build PATH order: validated MinGW bin first, GNU Make/PHP next, Git for Windows usr\\bin after MinGW (for sh.exe and Unix build utilities).");
        return result;
    }

    private static bool IsGitUsrBin(string path)
    {
        var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
               parts[^3].Equals("Git", StringComparison.OrdinalIgnoreCase) &&
               parts[^2].Equals("usr", StringComparison.OrdinalIgnoreCase) &&
               parts[^1].Equals("bin", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<List<string>> GetCommandPathsAsync(string command, IProgress<string> log, CancellationToken cancellationToken)
    {
        var script = $"$first = (Get-Command {command}).Source; if ($first) {{ $first }}; Get-Command {command} -All | Where-Object {{ $_.CommandType -eq 'Application' }} | ForEach-Object {{ $_.Source }}";
        log.Report($"PowerShell: (Get-Command {command}).Source and Get-Command {command} -All");
        var result = await CaptureAsync("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"", null, cancellationToken);
        if (result.ExitCode != 0) return new List<string>();
        return result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().Trim('"')).Where(File.Exists).ToList();
    }

    private static async Task<(int ExitCode, string Output)> CaptureAsync(string executable, string arguments, IDictionary<string, string?>? environment, CancellationToken cancellationToken, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo { FileName = executable, Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            start.WorkingDirectory = workingDirectory;
        if (environment is not null)
            foreach (var item in environment)
                if (item.Value is null) start.Environment.Remove(item.Key); else start.Environment[item.Key] = item.Value;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask + Environment.NewLine + await errorTask);
    }

    private static async Task<string> RunAsync(string executable, string arguments, string workingDirectory, IDictionary<string, string?>? environment, IProgress<string> log, CancellationToken cancellationToken, Action<string>? lineObserver = null)
    {
        var start = new ProcessStartInfo { FileName = executable, Arguments = arguments, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (environment is not null)
            foreach (var item in environment)
                if (item.Value is null) start.Environment.Remove(item.Key); else start.Environment[item.Key] = item.Value;

        var captured = new StringBuilder();
        var captureLock = new object();
        void Record(string? line)
        {
            if (line is null) return;
            lock (captureLock) captured.AppendLine(line);
            log.Report(line);
            lineObserver?.Invoke(line);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        log.Report($"> {Path.GetFileName(executable)} {arguments}");
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string output;
            lock (captureLock) output = captured.ToString();
            var moduleInfoError = Regex.Match(output, @"Error creating moduleinfo\.json file,\s*error=(\d+)", RegexOptions.IgnoreCase);
            if (moduleInfoError.Success && moduleInfoError.Groups[1].Value == "28")
                throw new InvalidOperationException(
                    "VST3 packaging failed while generating moduleinfo.json: upstream LSP status 28 is STATUS_INVALID_VALUE. This commonly indicates an invalid output path or serializer state; it is not Windows error 28 or a disk-space code. The builder now installs directly to the native absolute Windows PREFIX instead of incorrectly concatenating DESTDIR with it. If this status persists, the remaining log identifies the upstream metadata operation that failed. The source checkout and .build cache have been preserved.\n\nLast build output:\n" + LastLines(output, 35));
            throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}. Last build output:\n{LastLines(output, 35)}");
        }
        lock (captureLock) return captured.ToString();
    }

    private static bool MeetsMinimumVersion(string output, Version minimum)
    {
        var match = Regex.Match(output, @"\b(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success) return false;
        var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value),
            match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0);
        return version >= minimum;
    }

    private static string FirstLine(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(version unavailable)";
    private static string LastLines(string value, int count) => string.Join(Environment.NewLine, value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).TakeLast(count));

    private static async Task DeleteDirectorySafelyAsync(string path, CancellationToken cancellationToken)
    {
        const int attempts = 4;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(path)) return;

            try
            {
                // Git metadata (notably commit-graph-chain) can carry the read-only
                // attribute. Directory.Delete(recursive: true) does not clear it.
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.SetAttributes(directory, FileAttributes.Directory);
                }

                File.SetAttributes(path, FileAttributes.Directory);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException || ex is IOException) && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                throw new IOException(
                    $"Could not remove '{path}' after {attempts} attempts. A file may be locked by Git/antivirus, or the folder ACL may deny access. Close tools using the checkout and retry. Original error: {ex.Message}", ex);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
