using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LspWinGui.Services;

public sealed record UpstreamSource(string Sha, DateTime Date);
public sealed record BuildResult(int BundleCount, string InstallPath);
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
            throw new InvalidOperationException("GNU Make was not found. Install GNU Make from MSYS2 (for example, its usr\\bin\\make.exe) and ensure Get-Command make resolves it. Git for Windows usr\\bin tools are deliberately rejected.");

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
                var env = CreateBuildEnvironment(bin, Path.GetDirectoryName(make)!, Path.GetDirectoryName(php)!, null);
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

    public async Task<BuildResult> BuildAndInstallAsync(string commit, string toolchainRoot, string installPath, IProgress<string> log, CancellationToken cancellationToken = default)
    {
        var tools = await ValidateToolchainAsync(toolchainRoot, log, cancellationToken);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LSP-Win-GUI");
        var sourceDir = Path.Combine(root, "source");
        var stageDir = Path.Combine(root, "staging");
        Directory.CreateDirectory(root);
        if (Directory.Exists(sourceDir))
        {
            log.Report("Removing the previous source checkout (including read-only Git metadata)…");
            await DeleteDirectorySafelyAsync(sourceDir, cancellationToken);
        }
        if (Directory.Exists(stageDir))
            await DeleteDirectorySafelyAsync(stageDir, cancellationToken);
        Directory.CreateDirectory(stageDir);

        log.Report($"Cloning official source at {commit}…");
        await RunAsync(tools.Git, $"clone --filter=blob:none {Repository} \"{sourceDir}\"", root, null, log, cancellationToken);
        await RunAsync(tools.Git, $"checkout {commit}", sourceDir, null, log, cancellationToken);
        var actual = await CaptureAsync(tools.Git, "rev-parse HEAD", null, cancellationToken, sourceDir);
        if (actual.ExitCode != 0 || !actual.Output.Trim().Equals(commit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Source checkout mismatch. Requested {commit}, got {actual.Output.Trim()}.");

        var env = CreateBuildEnvironment(tools.CompilerBin, tools.MakeBin, Path.GetDirectoryName(tools.Php)!, log);
        log.Report("Cleaning previous build outputs…");
        await RunAsync(tools.Make, "clean", sourceDir, env, log, cancellationToken);
        log.Report("Configuring x64 VST3 + UI targets…");
        // Pass each feature as its own make variable. A space-containing FEATURES
        // assignment is re-emitted through upstream MAKEFLAGS and can be split
        // into an unintended make target on Windows.
        await RunAsync(tools.Make, "config FEATURES=vst3 ADD_FEATURES=ui ARCHITECTURE=x86_64 PREFIX=/usr/local", sourceDir, env, log, cancellationToken);
        var configPath = Path.Combine(sourceDir, ".config.mk");
        var configText = await File.ReadAllTextAsync(configPath, cancellationToken);
        if (!Regex.IsMatch(configText, @"(?m)^ARCHITECTURE=x86_64\\s*$") ||
            !Regex.IsMatch(configText, @"(?m)^HOST_ARCHITECTURE=x86_64\\s*$") ||
            !Regex.IsMatch(configText, @"(?m)^BUILD_FEATURES=.*\\bui\\b.*\\bvst3\\b"))
            throw new InvalidOperationException("Upstream configuration did not select x86_64 host/target architecture and both UI + VST3 features. Review .config.mk and the build log before continuing.");
        log.Report("Fetching upstream component repositories…");
        await RunAsync(tools.Make, "fetch", sourceDir, env, log, cancellationToken);
        log.Report("Compiling LSP plugins. This may take a while…");
        await RunAsync(tools.Make, "-j2", sourceDir, env, log, cancellationToken);
        log.Report("Staging installation…");
        await RunAsync(tools.Make, $"install DESTDIR=\"{stageDir.Replace('\\', '/')}\"", sourceDir, env, log, cancellationToken);

        var bundles = Directory.GetDirectories(stageDir, "*.vst3", SearchOption.AllDirectories);
        if (bundles.Length == 0)
            throw new InvalidOperationException("The build completed but no .vst3 bundles were found in the staged install. Nothing was installed.");

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

        log.Report($"Build complete. Installed {bundles.Length} bundle(s).");
        return new BuildResult(bundles.Length, installPath);
    }

    private static Dictionary<string, string?> CreateBuildEnvironment(string compilerBin, string makeBin, string phpBin, IProgress<string>? log)
    {
        var inherited = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim('"'))
            .Where(p => !IsGitUsrBin(p) && !string.IsNullOrWhiteSpace(p));
        var paths = new[] { compilerBin, makeBin, phpBin }.Concat(inherited).Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string?>
        {
            ["PATH"] = string.Join(Path.PathSeparator, paths),
            ["MSYSTEM"] = null,
            // The upstream build scripts use PROCESSOR_ARCHITECTURE when running
            // under native Windows rather than MinGW/MSYS. Force the validated
            // x64 toolchain target so both host and target builds are x64.
            ["PROCESSOR_ARCHITECTURE"] = "AMD64",
            ["CC"] = null,
            ["CXX"] = null,
            ["LD"] = null
        };
        log?.Report("Build PATH puts the validated MinGW bin directory first and excludes Git for Windows usr\\bin to prevent ld.exe collisions.");
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

    private static async Task RunAsync(string executable, string arguments, string workingDirectory, IDictionary<string, string?>? environment, IProgress<string> log, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = executable, Arguments = arguments, WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (environment is not null)
            foreach (var item in environment)
                if (item.Value is null) start.Environment.Remove(item.Key); else start.Environment[item.Key] = item.Value;
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.Report(e.Data); };
        log.Report($"> {Path.GetFileName(executable)} {arguments}");
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} exited with code {process.ExitCode}. Review the build log above.");
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
