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
    public bool MuteSounds { get; set; }
    public string OfflineTileColor { get; set; } = DefaultOfflineColor;
    public string OnlineTileColor { get; set; } = DefaultOnlineColor;
    public List<HostEntry> Hosts { get; set; } = new();
}

public static class SettingsService
{
    public static string Dir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseWatch");

    public static string FilePath { get; private set; } = Path.Combine(Dir, "settings.json");

    /// <summary>Use a different settings file (the --settings switch). Call before anything loads.</summary>
    public static void UseFile(string path)
    {
        FilePath = Path.GetFullPath(path);
        Dir = Path.GetDirectoryName(FilePath) ?? Dir;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <param name="warning">Set when the settings file existed but could not be used;
    /// the damaged file is preserved as settings.json.corrupt.</param>
    public static AppSettings Load(out string? warning)
    {
        warning = null;
        var fileExists = File.Exists(FilePath);
        if (fileExists)
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null)
                    return Sanitize(loaded);
            }
            catch
            {
                // fall through to backup + defaults
            }

            try
            {
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            }
            catch
            {
                // best effort
            }
            warning = $"The settings file could not be read and has been backed up as settings.json.corrupt in {Dir}. Starting with defaults.";
        }

        // First run (or unreadable file): seed starter targets.
        return new AppSettings
        {
            Hosts =
            {
                new HostEntry { Name = "Google DNS", Address = "8.8.8.8", CheckType = CheckType.Icmp },
                new HostEntry { Name = "Default Gateway", Address = "{gateway}", CheckType = CheckType.Icmp }
            }
        };
    }

    /// <summary>Clamps and repairs hand-edited or partially damaged values so they can't crash the app.</summary>
    private static AppSettings Sanitize(AppSettings s)
    {
        s.AlertDurationSeconds = Math.Clamp(s.AlertDurationSeconds, 1, 3600);
        if (ValidationHelpers.ParseColor(s.OfflineTileColor ?? "") == null) s.OfflineTileColor = AppSettings.DefaultOfflineColor;
        if (ValidationHelpers.ParseColor(s.OnlineTileColor ?? "") == null) s.OnlineTileColor = AppSettings.DefaultOnlineColor;

        s.Hosts ??= new List<HostEntry>();
        s.Hosts.RemoveAll(h => h == null);

        var seenIds = new HashSet<Guid>();
        foreach (var h in s.Hosts)
        {
            if (h.Id == Guid.Empty || !seenIds.Add(h.Id))
            {
                h.Id = Guid.NewGuid(); // duplicate ids would cross-wire loops and tile dismissal
                seenIds.Add(h.Id);
            }
            h.Name ??= "";
            h.Address ??= "";
            h.Port = Math.Clamp(h.Port, 1, 65535);
            h.IntervalSeconds = Math.Clamp(h.IntervalSeconds, 1, 86400);
            h.RetryCount = Math.Clamp(h.RetryCount, 0, 10);
            h.TimeoutMs = Math.Clamp(h.TimeoutMs, 100, 60000);
            if (h.OfflineColor != null && ValidationHelpers.ParseColor(h.OfflineColor) == null) h.OfflineColor = null;
            if (h.OnlineColor != null && ValidationHelpers.ParseColor(h.OnlineColor) == null) h.OnlineColor = null;
        }
        return s;
    }

    /// <summary>Atomic write (temp file + rename); returns false if saving failed.</summary>
    public static bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
