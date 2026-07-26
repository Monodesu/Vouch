using System;
using System.Linq;
using Vouch.Core.Steam;

namespace Vouch.App.Models;

public enum ConfirmationKind
{
    Trade,
    MarketListing,
    AccountRecovery,
    ApiKey,
    Login
}

public class ConfirmationItem
{
    public required ConfirmationKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string When { get; init; }

    /// <summary>The real Steam confirmation this item represents, if any (null for demo items).</summary>
    public SteamConfirmation? Source { get; init; }

    /// <summary>The pending login this item represents, if it's a login-approval request (else null).</summary>
    public PendingLoginSession? LoginSession { get; init; }

    /// <summary>True for login-approval rows — they open a detail dialog on click; others don't.</summary>
    public bool IsLogin => Kind == ConfirmationKind.Login;

    public string Glyph => Kind switch
    {
        ConfirmationKind.Trade => "\U0001F501",           // repeat
        ConfirmationKind.MarketListing => "\U0001F3F7",   // tag
        ConfirmationKind.AccountRecovery => "\U0001F512", // lock
        ConfirmationKind.ApiKey => "\U0001F511",          // key
        ConfirmationKind.Login => "\U0001F4BB",           // laptop
        _ => "❓"
    };

    /// <summary>Builds a confirmation-list item for a pending login approval.</summary>
    public static ConfirmationItem FromLogin(PendingLoginSession s) => new()
    {
        Kind = ConfirmationKind.Login,
        Title = string.IsNullOrWhiteSpace(s.DeviceName)
            ? Localization.Loc.T("Login_ApprovalTitle")
            : Localization.Loc.T("Login_ApprovalTitleNamed", s.FriendlyDevice),
        Subtitle = string.Join(" · ", new[] { s.Location, s.Ip }.Where(x => x.Length > 0)),
        When = "",
        LoginSession = s
    };

    public static ConfirmationItem FromSteam(SteamConfirmation c) => new()
    {
        Kind = ClassifyKind(c.TypeName),
        Title = string.IsNullOrWhiteSpace(c.Headline) ? c.TypeName : c.Headline,
        Subtitle = string.Join(" · ", c.Summary),
        When = RelativeTime(c.CreationTime),
        Source = c
    };

    private static ConfirmationKind ClassifyKind(string typeName)
    {
        var t = typeName.ToLowerInvariant();
        if (t.Contains("market")) return ConfirmationKind.MarketListing;
        if (t.Contains("recovery") || t.Contains("account")) return ConfirmationKind.AccountRecovery;
        if (t.Contains("api")) return ConfirmationKind.ApiKey;
        return ConfirmationKind.Trade;
    }

    private static string RelativeTime(long unixSeconds)
    {
        if (unixSeconds <= 0) return "";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}
