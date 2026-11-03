using System.IO;
using System.Text.Json;

namespace LspWinGui.Services;

public sealed class AppSettings
{
    public string InstallPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files", "VST3");
    public string ToolchainRoot { get; set; } = "";
    public string LastBuiltCommit { get; set; } = "";
    public string LastCheckedCommit { get; set; } = "";
    public DateTimeOffset? LastCheckedAt { get; set; }
}

public sealed class SettingsService
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LSP-Win-GUI");
    private string FilePath => Path.Combine(_directory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
