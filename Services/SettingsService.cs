using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectionChecker.Models;

namespace ConnectionChecker.Services;

public class AppSettings
{
    public const string DefaultOfflineColor = "#FF3B5C";
    public const string DefaultOnlineColor = "#22E584";

    public int AlertDurationSeconds { get; set; } = 10;
    public bool CloseToTray { get; set; } = false;
    public bool ConfirmOnExit { get; set; } = true;
    public string OfflineTileColor { get; set; } = DefaultOfflineColor;
    public string OnlineTileColor { get; set; } = DefaultOnlineColor;
    public List<HostEntry> Hosts { get; set; } = new();
}

public static class SettingsService
{
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseWatch");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt settings file — start fresh rather than crash.
        }

        // First run (or unreadable file): seed a starter target.
        return new AppSettings
        {
            Hosts =
            {
                new HostEntry { Name = "Google DNS", Address = "8.8.8.8", CheckType = CheckType.Icmp }
            }
        };
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Non-fatal: monitoring still works without persistence.
        }
    }
}
