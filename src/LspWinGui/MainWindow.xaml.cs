using System.IO;
using System.Windows;
using LspWinGui.Services;

namespace LspWinGui;

public partial class MainWindow : Window
{
    private readonly LspBuildService _builder = new();
    private readonly SettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private string? _latestCommit;
    private string? _lastErrorDetails;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        InstallPathBox.Text = _settings.InstallPath;
        ToolchainPathBox.Text = _settings.ToolchainRoot;
        _latestCommit = string.IsNullOrWhiteSpace(_settings.LastCheckedCommit) ? null : _settings.LastCheckedCommit;

        VersionText.Text = $"Installed source: {ShortSha(_settings.LastBuiltCommit)}  ·  Latest checked: {ShortSha(_settings.LastCheckedCommit)}";
        StatusText.Text = string.IsNullOrWhiteSpace(_settings.LastBuiltCommit)
            ? "No successful source build recorded yet."
            : "Saved build version loaded. Check upstream for updates.";
        AppendLog("Ready. Settings and source version are stored in %LOCALAPPDATA%\\LSP-Win-GUI\\settings.json.");
        AppendLog($"Last successfully installed source commit: {(_settings.LastBuiltCommit.Length == 0 ? "(none)" : _settings.LastBuiltCommit)}");
        AppendLog("Build preflight discovers gcc using PowerShell Get-Command and tests the C and C++ compiler/linker before touching source.");
        BuildButton.IsEnabled = _latestCommit is not null;
        Closing += (_, _) => SaveSettings();
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        ClearErrorDetails();
        SetBusy(true, "Checking upstream source…");
        try
        {
            var source = await _builder.GetLatestSourceAsync();
            _latestCommit = source.Sha;
            _settings.LastCheckedCommit = source.Sha;
            _settings.LastCheckedAt = DateTimeOffset.UtcNow;
            SaveSettings();
            var updateAvailable = !string.IsNullOrWhiteSpace(_settings.LastBuiltCommit) &&
                !source.Sha.Equals(_settings.LastBuiltCommit, StringComparison.OrdinalIgnoreCase);
            VersionText.Text = $"Installed: {ShortSha(_settings.LastBuiltCommit)}  ·  Latest: {ShortSha(source.Sha)} · {source.Date:yyyy-MM-dd HH:mm} UTC";
            StatusText.Text = string.IsNullOrWhiteSpace(_settings.LastBuiltCommit)
                ? "Latest source found. No installed build version recorded yet."
                : updateAvailable ? "Update available — upstream source has changed." : "Up to date — installed source matches upstream.";
            AppendLog($"Latest upstream commit: {source.Sha}");
            AppendLog($"Commit date: {source.Date:O}");
            AppendLog($"Last successful local build: {(_settings.LastBuiltCommit.Length == 0 ? "(none)" : _settings.LastBuiltCommit)}");
            AppendLog(updateAvailable ? "UPDATE AVAILABLE: a newer upstream commit is available." : "No source update detected.");
            BuildButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not check upstream source.";
            AppendLog($"ERROR: {ex}");
            ShowErrorDetails("Source check failed", ex);
        }
        finally { SetBusy(false); }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the VST3 installation folder", InitialDirectory = Directory.Exists(InstallPathBox.Text) ? InstallPathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        if (dialog.ShowDialog(this) == true) { InstallPathBox.Text = dialog.FolderName; SaveSettings(); }
    }

    private void ToolchainBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose MinGW-w64 root or its bin folder", InitialDirectory = Directory.Exists(ToolchainPathBox.Text) ? ToolchainPathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        if (dialog.ShowDialog(this) == true) { ToolchainPathBox.Text = dialog.FolderName; SaveSettings(); }
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
        ClearErrorDetails();
        if (string.IsNullOrWhiteSpace(_latestCommit))
        {
            MessageBox.Show(this, "Check upstream source first.", "LSP Win GUI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(InstallPathBox.Text))
        {
            MessageBox.Show(this, "Choose a VST3 installation folder.", "LSP Win GUI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings();
        SetBusy(true, "Validating Windows build tools…");
        BuildProgress.IsIndeterminate = true;
        BuildProgress.Value = 0;
        BuildDetailText.Text = "Validating toolchain and preparing reusable source/build cache…";
        try
        {
            var progress = new Progress<string>(AppendLog);
            var buildProgress = new Progress<BuildProgressUpdate>(update =>
            {
                BuildProgress.IsIndeterminate = update.IsIndeterminate;
                BuildProgress.Value = Math.Clamp(update.Percent, 0, 100);
                StatusText.Text = update.Status;
                BuildDetailText.Text = update.Detail;
            });
            var result = await _builder.BuildAndInstallAsync(
                _latestCommit, ToolchainPathBox.Text, InstallPathBox.Text, progress,
                buildProgress: buildProgress);
            _settings.LastBuiltCommit = _latestCommit;
            SaveSettings();
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Value = 100;
            BuildDetailText.Text = $"Completed · {result.BundleCount} VST3 bundle(s) installed to {result.InstallPath}";
            StatusText.Text = $"Installed {result.BundleCount} VST3 bundle(s) from {ShortSha(_settings.LastBuiltCommit)}.";
            VersionText.Text = $"Installed: {ShortSha(_settings.LastBuiltCommit)}  ·  Latest: {ShortSha(_latestCommit)}";
            AppendLog($"Installed to: {result.InstallPath}");
            AppendLog($"Saved successful source commit: {_settings.LastBuiltCommit}");
            MessageBox.Show(this, $"Successfully installed {result.BundleCount} VST3 bundle(s).", "Build complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            BuildProgress.IsIndeterminate = false;
            BuildDetailText.Text = "Build stopped at the failed stage. The source checkout and incremental build cache were kept for the next attempt.";
            StatusText.Text = "Build failed. The saved installed version was not changed.";
            AppendLog($"BUILD FAILED: {ex}");
            ShowErrorDetails("Build failed", ex);
        }
        finally { SetBusy(false); }
    }

    private void ClearErrorDetails()
    {
        _lastErrorDetails = null;
        CopyErrorButton.Visibility = Visibility.Collapsed;
    }

    private void ShowErrorDetails(string title, Exception exception)
    {
        _lastErrorDetails = exception.ToString() + Environment.NewLine + Environment.NewLine +
            "========== APPLICATION LOG ==========" + Environment.NewLine + LogBox.Text;
        CopyErrorButton.Visibility = Visibility.Visible;
        try
        {
            Clipboard.SetText(_lastErrorDetails);
            StatusText.Text = "An error occurred. Full details copied to clipboard.";
        }
        catch
        {
            StatusText.Text = "An error occurred. Use Copy error details to copy the log.";
        }
        MessageBox.Show(this, exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void CopyErrorButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastErrorDetails)) return;
        try
        {
            Clipboard.SetText(_lastErrorDetails);
            StatusText.Text = "Full error details copied to clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not access the clipboard: {ex.Message}", "Clipboard error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveSettings()
    {
        _settings.InstallPath = InstallPathBox.Text;
        _settings.ToolchainRoot = ToolchainPathBox.Text;
        _settingsService.Save(_settings);
    }

    private static string ShortSha(string? sha) =>
        string.IsNullOrWhiteSpace(sha) ? "not built" : sha[..Math.Min(12, sha.Length)];

    private void AppendLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
    }

    private void SetBusy(bool busy, string? status = null)
    {
        CheckButton.IsEnabled = !busy;
        BuildButton.IsEnabled = !busy && _latestCommit is not null;
        if (status is not null) StatusText.Text = status;
    }
}
