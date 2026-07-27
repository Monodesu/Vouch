using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Vouch.App.ViewModels;

public partial class AccountViewModel : ObservableObject
{
    private static readonly Color[] Palette =
    {
        Color.Parse("#66C0F4"), Color.Parse("#A4D007"), Color.Parse("#E28B3E"),
        Color.Parse("#D46A9A"), Color.Parse("#8C7AE6")
    };

    public string SteamId { get; }
    public string Username { get; }

    private string _password;
    /// <summary>The Steam account password. Writing it keeps the backing maFile model in sync so a
    /// <c>repo.Save</c> persists it; the caller owns the save.</summary>
    public string Password
    {
        get => _password;
        set
        {
            _password = value;
            if (Model is not null) Model.AccountPassword = value;
        }
    }

    private string _notes = "";
    /// <summary>Free-text note for this account. Writing it keeps the backing maFile model in sync; the
    /// caller persists via <c>repo.Save</c> (see MainViewModel.SaveAccountNotes).</summary>
    public string Notes
    {
        get => _notes;
        set
        {
            _notes = value;
            if (Model is not null) Model.AccountNotes = value;
        }
    }

    public byte[] SharedSecret { get; private set; }

    // Populated for real (imported) accounts; null for mock ones.
    public string? IdentitySecret { get; private set; }
    public string? RevocationCode { get; private set; }
    public bool IsReal { get; private set; }

    /// <summary>The underlying maFile model for real accounts (so its session can be updated + re-saved).</summary>
    public Vouch.Core.Steam.SteamGuardAccount? Model { get; private set; }

    /// <summary>This account's Steam web session, or null when it's never been signed in.</summary>
    public Vouch.Core.Steam.SessionData? Session => Model?.Session;

    /// <summary>The session's current access token, or null when the account has no session.</summary>
    public string? AccessToken => Session?.AccessToken;

    // ---- session indicator (sidebar stripe) ----
    // Green = signed in (a live access OR refresh token — right after enrollment there's only an access
    // token), Yellow = signed in before but every token has expired, Red = never signed in (no tokens).

    private static bool TokenAlive(string? jwt) =>
        !string.IsNullOrEmpty(jwt) && !Vouch.Core.Steam.SteamSessionService.IsExpired(jwt, TimeSpan.Zero);

    private bool HasRefresh => !string.IsNullOrEmpty(Session?.RefreshToken);
    private bool HasAnyToken => HasRefresh || !string.IsNullOrEmpty(Session?.AccessToken);

    /// <summary>Set when a session op (renew / web-session handshake) confirmed the session is dead even
    /// though a token may still look time-valid (e.g. a revoked-but-unexpired refresh token). Cleared by a
    /// successful renew/login. In-memory only.</summary>
    [ObservableProperty] private bool _sessionInvalid;
    partial void OnSessionInvalidChanged(bool value) => RefreshSessionState();

    /// <summary>When the session was last authoritatively re-checked (a renew). Throttles background checks.</summary>
    internal DateTimeOffset LastRevalidated { get; set; }

    /// <summary>Green: signed in. The refresh token is the real ~200-day credential, so if one exists it
    /// must be alive (an expired refresh means the session can't be sustained even if the short-lived
    /// access token still looks valid); right after enrolling there's only an access token, so fall back
    /// to that.</summary>
    public bool SessionOk => !SessionInvalid
        && (HasRefresh ? TokenAlive(Session!.RefreshToken) : TokenAlive(Session?.AccessToken));

    /// <summary>Red: never signed in — no tokens at all.</summary>
    public bool SessionOut => !SessionInvalid && !HasAnyToken;

    /// <summary>Yellow: was signed in but the session expired / was confirmed dead — needs re-login.</summary>
    public bool SessionWarn => !SessionOk && !SessionOut;

    /// <summary>Re-evaluates the session indicator (call after the session tokens change — login/renew).</summary>
    public void RefreshSessionState()
    {
        OnPropertyChanged(nameof(SessionOk));
        OnPropertyChanged(nameof(SessionWarn));
        OnPropertyChanged(nameof(SessionOut));
    }

    // Background-notification de-dup (in-memory): highest confirmation / incoming-offer id already toasted.
    // Baseline flags suppress a toast on the first sweep so startup doesn't announce pre-existing items.
    public bool ConfBaselineDone { get; set; }
    public ulong LastConfId { get; set; }
    public bool OfferBaselineDone { get; set; }
    public ulong LastOfferId { get; set; }
    // Login-approval requests carry non-monotonic client_ids, so we de-dup by a seen-set rather than a max.
    public bool LoginBaselineDone { get; set; }
    public HashSet<ulong> SeenLoginIds { get; } = new();

    [ObservableProperty] private bool _hasSession;

    /// <summary>The sidebar group this account belongs to; "" is the default group.</summary>
    [ObservableProperty] private string _group = "";

    /// <summary>True while this row is being dragged in the sidebar — dims it under the drag ghost.</summary>
    [ObservableProperty] private bool _isDragging;

    private readonly string _avatarAsset;
    private readonly string _freshPersona;
    private readonly int _fetchedVacBans;
    private readonly int _fetchedGameBans;

    // PersonaName is the Steam display name; it can be refreshed from Steam.
    [ObservableProperty] private string _personaName;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private int _pendingConfirmations;
    [ObservableProperty] private bool _infoUpdated;

    // -1 = not fetched yet. Set from ISteamUser/GetPlayerBans on update.
    [ObservableProperty] private int _vacBans = -1;
    [ObservableProperty] private int _gameBans = -1;
    [ObservableProperty] private bool _tradeBanned;

    public string Initials =>
        PersonaName.Length >= 2 ? PersonaName[..2].ToUpperInvariant()
                                : PersonaName.ToUpperInvariant();

    public IBrush AvatarBrush { get; }
    public bool HasAvatar => Avatar is not null;
    public bool HasPending => PendingConfirmations > 0;
    public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId}";

    public bool HasBanInfo => GameBans >= 0 || VacBans >= 0;
    public bool IsClean => HasBanInfo && GameBans <= 0 && VacBans <= 0 && !TradeBanned;
    public bool HasGameBan => GameBans > 0;
    public bool HasVacBan => VacBans > 0;
    public string GameBanText => GameBans == 1 ? "1 game ban" : $"{GameBans} game bans";
    public string VacBanText => VacBans == 1 ? "VAC banned" : $"{VacBans} VAC bans";

    public AccountViewModel(string name, string steamId, int pending, int paletteIndex,
                            string username, string password, string freshPersona,
                            int vacBans, int gameBans)
    {
        PersonaName = name;
        SteamId = steamId;
        Username = username;
        _password = password;
        _freshPersona = freshPersona;
        _fetchedVacBans = vacBans;
        _fetchedGameBans = gameBans;
        _avatarAsset = $"avares://Vouch/Assets/avatars/av{paletteIndex}.png";
        _pendingConfirmations = pending;
        AvatarBrush = new SolidColorBrush(Palette[paletteIndex % Palette.Length]);
        SharedSecret = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(steamId));
    }

    /// <summary>Builds a real account from an imported maFile. Codes are generated from its true secret.</summary>
    public static AccountViewModel FromMaFile(Vouch.Core.Steam.SteamGuardAccount a, int paletteIndex)
    {
        var name = string.IsNullOrWhiteSpace(a.AccountName) ? "unknown" : a.AccountName!;
        var steamId = a.Session?.SteamId.ToString() ?? "0";
        // bans/persona unknown until a real Steam fetch — pass -1 / same name.
        var vm = new AccountViewModel(name, steamId, 0, paletteIndex, name, a.AccountPassword ?? "", name, -1, -1)
        {
            SharedSecret = Convert.FromBase64String(a.SharedSecret!),
            IdentitySecret = a.IdentitySecret,
            RevocationCode = a.RevocationCode,
            IsReal = true,
            Model = a,
            Notes = a.AccountNotes ?? "",
            HasSession = !string.IsNullOrEmpty(a.Session?.AccessToken)
        };
        return vm;
    }

    /// <summary>Applies real profile info fetched from Steam's community XML (name, avatar, VAC/trade ban).</summary>
    public void ApplyProfile(string persona, Bitmap? avatar, bool vacBanned, bool tradeBanned)
    {
        if (!string.IsNullOrWhiteSpace(persona)) PersonaName = persona;
        if (avatar is not null) Avatar = avatar;
        VacBans = vacBanned ? 1 : 0;
        if (GameBans < 0) GameBans = 0; // XML has no game-ban count; assume none until the API says otherwise
        TradeBanned = tradeBanned;
        InfoUpdated = true;
        OnPropertyChanged(nameof(Initials));
    }

    /// <summary>Restores cached profile info at startup — same as ApplyProfile but without the "updated just now" flash.</summary>
    public void ApplyCachedProfile(string? persona, Bitmap? avatar, int vacBans, int gameBans, bool tradeBanned)
    {
        if (!string.IsNullOrWhiteSpace(persona)) PersonaName = persona!;
        if (avatar is not null) Avatar = avatar;
        if (vacBans >= 0) VacBans = vacBans;
        if (gameBans >= 0) GameBans = gameBans;
        TradeBanned = tradeBanned;
        OnPropertyChanged(nameof(Initials));
    }

    /// <summary>Applies exact ban counts from ISteamUser/GetPlayerBans (needs a Web API key).</summary>
    public void ApplyBans(int vacBans, int gameBans)
    {
        VacBans = vacBans;
        GameBans = gameBans;
    }

    /// <summary>Simulates fetching persona name + avatar + ban status from Steam.</summary>
    public void ApplyFetchedInfo()
    {
        PersonaName = _freshPersona;
        Avatar = new Bitmap(AssetLoader.Open(new Uri(_avatarAsset)));
        VacBans = _fetchedVacBans;
        GameBans = _fetchedGameBans;
        InfoUpdated = true;
        OnPropertyChanged(nameof(Initials));
    }

    partial void OnHasSessionChanged(bool value) => RefreshSessionState();
    partial void OnPendingConfirmationsChanged(int value) => OnPropertyChanged(nameof(HasPending));
    partial void OnAvatarChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAvatar));
    partial void OnPersonaNameChanged(string value) => OnPropertyChanged(nameof(Initials));

    partial void OnGameBansChanged(int value)
    {
        OnPropertyChanged(nameof(HasBanInfo));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(HasGameBan));
        OnPropertyChanged(nameof(GameBanText));
    }

    partial void OnVacBansChanged(int value)
    {
        OnPropertyChanged(nameof(HasBanInfo));
        OnPropertyChanged(nameof(IsClean));
        OnPropertyChanged(nameof(HasVacBan));
        OnPropertyChanged(nameof(VacBanText));
    }

    partial void OnTradeBannedChanged(bool value) => OnPropertyChanged(nameof(IsClean));
}
