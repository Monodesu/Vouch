using System.Text.Json.Serialization;

namespace Vouch.Core.Storage;

/// <summary>
/// <c>maFiles/entries.json</c> — the account index and the sidebar's presentation state, so the
/// layout travels with the maFiles directory rather than living in app settings. Account order is the
/// order of <see cref="Accounts"/>; each account carries its group; <see cref="Groups"/> records the
/// group order and collapsed state (including empty groups). The default group ("") is implicit and
/// never stored. Whether the directory is encrypted lives in app settings; per-file encryption is
/// self-contained (v2 envelope), so nothing about the ciphertext is kept here.
/// </summary>
public class MaFileIndex
{
    [JsonPropertyName("accounts")] public List<MaFileEntry> Accounts { get; set; } = new();
    [JsonPropertyName("groups")] public List<GroupEntry> Groups { get; set; } = new();
}

/// <summary>One indexed account: which file holds it, its SteamID, and its sidebar group
/// (null/empty = the default group).</summary>
public class MaFileEntry
{
    [JsonPropertyName("filename")] public string? Filename { get; set; }
    [JsonPropertyName("steamid")] public ulong SteamId { get; set; }
    [JsonPropertyName("group")] public string? Group { get; set; }
}

/// <summary>A custom sidebar group: its name and whether it's collapsed. Order is the list position.</summary>
public class GroupEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("collapsed")] public bool Collapsed { get; set; }
}
