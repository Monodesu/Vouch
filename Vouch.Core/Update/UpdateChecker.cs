using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vouch.Core.Update;

/// <summary>A GitHub release: its tag, parsed version, page URL, and whether it's a pre-release.</summary>
public record ReleaseInfo(string Tag, Version Version, string Url, bool Prerelease);

/// <summary>
/// Checks a GitHub repo's latest release for a newer version. Tags are expected in <c>v0.0.0</c>
/// form (a leading "v" and pre-release/build suffixes are tolerated). Parsing is pure/testable;
/// the fetch swallows network errors and returns null so a failed check never disrupts the app.
/// </summary>
public class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vouch-UpdateCheck/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Fetches the repo's latest (non-prerelease) release, or null on any error / no releases.</summary>
    public async Task<ReleaseInfo?> FetchLatestAsync(string owner, string repo, CancellationToken ct = default)
    {
        try
        {
            var json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct);
            return ParseLatestJson(json);
        }
        catch (Exception) { return null; }
    }

    // ---- parsing (pure, testable) ----

    public static ReleaseInfo? ParseLatestJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString() ?? "";
            if (ParseTag(tag) is not { } version) return null;

            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var pre = root.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            return new ReleaseInfo(tag, version, url, pre);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>"v1.2.3" / "1.2" / "v1.2.3-beta" → Version(1,2,3); null when no version is present.</summary>
    public static Version? ParseTag(string tag)
    {
        var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        int patch = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), patch);
    }

    /// <summary>True if <paramref name="latest"/> is a newer version than <paramref name="current"/>
    /// (compared on major.minor.patch, treating an unset build as 0).</summary>
    public static bool IsNewer(Version latest, Version current)
    {
        static Version Norm(Version v) => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
        return Norm(latest) > Norm(current);
    }
}
