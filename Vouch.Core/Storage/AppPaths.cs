namespace Vouch.Core.Storage;

/// <summary>
/// Where the app keeps its data. Portable by default: everything (maFiles, settings, cache) lives
/// next to the exe. Override the whole location with the <c>VOUCH_DATA_DIR</c> env var (tests/screenshots).
/// </summary>
public static class AppPaths
{
    public static string DataDir =>
        Environment.GetEnvironmentVariable("VOUCH_DATA_DIR") ?? AppContext.BaseDirectory;

    public static string MaFilesDir => Path.Combine(DataDir, "maFiles");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static string CacheDir => Path.Combine(DataDir, "cache");
}
