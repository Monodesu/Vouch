using System.Text.Json;

namespace Vouch.Core.Storage;

/// <summary>App settings, persisted as <c>settings.json</c> in the data directory.</summary>
public class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "en";
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool PeriodicChecking { get; set; } = true;
    public int PeriodicIntervalSeconds { get; set; } = 30;   // selected account, min 10
    public int SweepIntervalSeconds { get; set; } = 120;     // all accounts (badges), min 60
    public int ClipboardClearSeconds { get; set; } = 15;
    public string ApiKey { get; set; } = "";
    public string TradeUrl { get; set; } = ""; // target trade link for inventory transfer
    public bool NotifyOnNew { get; set; } = true; // system notification on new confirmation/offer
    public bool Encrypted { get; set; } // whether the maFiles directory is encrypted at rest

    // Account order + groups used to live here; they moved to maFiles/entries.json so the layout
    // travels with the accounts. See MaFileIndex; MainViewModel migrates any legacy fields once.

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) is { } s)
                return s;
        }
        catch (Exception) { } // corrupt settings file -> fall back to defaults
        return new AppSettings();
    }

    public void SaveTo(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
