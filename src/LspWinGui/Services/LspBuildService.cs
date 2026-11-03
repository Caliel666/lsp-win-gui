using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LspWinGui.Services;

public sealed record UpstreamSource(string Sha, DateTime Date);
public sealed record BuildResult(int BundleCount, string InstallPath);

public sealed class LspBuildService
{
    private const string Repository = "https://github.com/lsp-plugins/lsp-plugins.git";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LSP-Win-GUI", "0.1.0"));
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

    public async Task<BuildResult> BuildAndInstallAsync(string commit, string toolchainRoot, string installPath, IProgress<string> log, CancellationToken cancellationToken = default)
    {
        var git = FindExecutable("git.exe", "git");
        var make = FindExecutable("make.exe", "make", Path.Combine(toolchainRoot, "bin"));
        var gcc = FindExecutable("gcc.exe", "gcc", Path.Combine(toolchainRoot, "bin"));
        var php = FindExecutable("php.exe", "php");
        if (git is null || make is null || gcc is null || php is null)
            throw new InvalidOperationException("Build tools are missing. Install Git, MinGW-w64 GCC, GNU Make, and PHP, then add them to PATH. The MinGW folder should contain bin\\gcc.exe.");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LSP-Win-GUI");
        var sourceDir = Path.Combine(root, "source");
        var stageDir = Path.Combine(root, "staging");
        Directory.CreateDirectory(root);
        if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
        Directory.CreateDirectory(stageDir);

        log.Report($"Cloning official source: {commit}");
        await RunAsync(git, $"clone --filter=blob:none {Repository} \"{sourceDir}\"", root, null, log, cancellationToken);
        await RunAsync(git, $"checkout {commit}", sourceDir, null, log, cancellationToken);

        var gitDirectory = Path.GetDirectoryName(git) ?? "";
        var gitUsrBin = Path.GetFullPath(Path.Combine(gitDirectory, "..", "usr", "bin"));
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = string.Join(Path.PathSeparator, new[] { Path.Combine(toolchainRoot, "bin"), gitDirectory, Directory.Exists(gitUsrBin) ? gitUsrBin : "", Path.GetDirectoryName(php) ?? "", Environment.GetEnvironmentVariable("PATH") ?? "" }.Where(x => !string.IsNullOrWhiteSpace(x))),
            ["MSYSTEM"] = null
        };

        log.Report("Cleaning previous build outputs…");
        await RunAsync(make, "clean", sourceDir, env, log, cancellationToken);
        log.Report("Configuring x64 VST3 + UI targets…");
        await RunAsync(make, "config FEATURES=\"vst3 ui\" PREFIX=/usr/local", sourceDir, env, log, cancellationToken);
        log.Report("Fetching upstream component repositories…");
        await RunAsync(make, "fetch", sourceDir, env, log, cancellationToken);
        log.Report("Compiling LSP plugins. This may take a while…");
        await RunAsync(make, "-j2", sourceDir, env, log, cancellationToken);
        log.Report("Staging installation…");
        await RunAsync(make, $"install DESTDIR=\"{stageDir.Replace('\\', '/')}\"", sourceDir, env, log, cancellationToken);

        var bundles = Directory.GetDirectories(stageDir, "*.vst3", SearchOption.AllDirectories);
        if (bundles.Length == 0)
            throw new InvalidOperationException("The build completed but no .vst3 bundles were found in the staged install. Nothing was installed.");

        Directory.CreateDirectory(installPath);
        var backup = Path.Combine(root, "backup");
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
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

    private static string? FindExecutable(string fileName, string command, string? extraDirectory = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(extraDirectory)) candidates.Add(Path.Combine(extraDirectory, fileName));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p.Trim('"'), fileName)));
        var found = candidates.FirstOrDefault(File.Exists);
        return found;
    }

    private static async Task RunAsync(string executable, string arguments, string workingDirectory, IDictionary<string, string?>? environment, IProgress<string> log, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
