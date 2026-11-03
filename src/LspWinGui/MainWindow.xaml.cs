using System.IO;
using System.Windows;
using System.Windows.Forms;
using LspWinGui.Services;

namespace LspWinGui;

public partial class MainWindow : Window
{
    private readonly LspBuildService _builder = new();
    private string? _latestCommit;

    public MainWindow()
    {
        InitializeComponent();
        AppendLog("Ready. The application builds source from the official lsp-plugins GitHub repository.");
        AppendLog("Build prerequisites: Git, MinGW-w64 GCC, GNU Make 4.2+, and PHP 5.5.14+.");
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Checking upstream source…");
        try
        {
            var source = await _builder.GetLatestSourceAsync();
            _latestCommit = source.Sha;
            VersionText.Text = $"Latest commit: {source.Sha[..Math.Min(12, source.Sha.Length)]} · {source.Date:yyyy-MM-dd HH:mm} UTC";
            StatusText.Text = "Source check complete. Ready to build.";
            AppendLog($"Latest upstream commit: {source.Sha}");
            AppendLog($"Commit date: {source.Date:O}");
            BuildButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not check upstream source.";
            AppendLog($"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Source check failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the VST3 installation folder", SelectedPath = InstallPathBox.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) InstallPathBox.Text = dialog.SelectedPath;
    }

    private void ToolchainBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose your MinGW-w64 installation folder", SelectedPath = ToolchainPathBox.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) ToolchainPathBox.Text = dialog.SelectedPath;
    }

    private async void BuildButton_Click(object sender, RoutedEventArgs e)
    {
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

        SetBusy(true, "Preparing build…");
        BuildProgress.IsIndeterminate = true;
        try
        {
            var progress = new Progress<string>(AppendLog);
            var result = await _builder.BuildAndInstallAsync(_latestCommit, ToolchainPathBox.Text, InstallPathBox.Text, progress);
            BuildProgress.IsIndeterminate = false;
            BuildProgress.Value = 100;
            StatusText.Text = $"Installed {result.BundleCount} VST3 bundle(s) successfully.";
            AppendLog($"Installed to: {result.InstallPath}");
            MessageBox.Show(this, $"Successfully installed {result.BundleCount} VST3 bundle(s).", "Build complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            BuildProgress.IsIndeterminate = false;
            StatusText.Text = "Build failed. Existing installed plugins were left untouched.";
            AppendLog($"BUILD FAILED: {ex}");
            MessageBox.Show(this, ex.Message, "Build failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

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
