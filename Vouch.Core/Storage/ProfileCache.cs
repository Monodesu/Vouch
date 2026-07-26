using System.Text.Json;

namespace Vouch.Core.Storage;

/// <summary>Cached Steam profile info (persona, bans) — refreshed by "Update info".</summary>
public class CachedProfile
{
    public string? PersonaName { get; set; }
    public int VacBans { get; set; } = -1;
    public int GameBans { get; set; } = -1;
    public bool TradeBanned { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>
/// Persists fetched profile info + avatar images per account so persona names and avatars
/// survive restarts (<c>cache/{steamid}.json</c> + <c>{steamid}.avatar</c> in the data dir).
/// Cache misses/corruption just mean "not fetched yet" — never fatal.
/// </summary>
public class ProfileCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string CacheDir { get; }

    public ProfileCache(string cacheDir)
    {
        CacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
    }

    private string JsonPath(ulong steamId) => Path.Combine(CacheDir, $"{steamId}.json");
    private string AvatarPath(ulong steamId) => Path.Combine(CacheDir, $"{steamId}.avatar");

    public void Save(ulong steamId, CachedProfile profile, byte[]? avatar)
    {
        try
        {
            File.WriteAllText(JsonPath(steamId), JsonSerializer.Serialize(profile, Json));
            if (avatar is { Length: > 0 })
                File.WriteAllBytes(AvatarPath(steamId), avatar);
        }
        catch (Exception) { } // cache only — losing it costs one re-fetch
    }

    public (CachedProfile? Profile, byte[]? Avatar) Load(ulong steamId)
    {
        CachedProfile? profile = null;
        byte[]? avatar = null;
        try
        {
            if (File.Exists(JsonPath(steamId)))
                profile = JsonSerializer.Deserialize<CachedProfile>(File.ReadAllText(JsonPath(steamId)));
            if (File.Exists(AvatarPath(steamId)))
                avatar = File.ReadAllBytes(AvatarPath(steamId));
        }
        catch (Exception) { }
        return (profile, avatar);
    }

    public void Delete(ulong steamId)
    {
        try
        {
            if (File.Exists(JsonPath(steamId))) File.Delete(JsonPath(steamId));
            if (File.Exists(AvatarPath(steamId))) File.Delete(AvatarPath(steamId));
        }
        catch (Exception) { }
    }
}
