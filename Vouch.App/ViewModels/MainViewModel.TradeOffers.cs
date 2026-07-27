using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Steam;

namespace Vouch.App.ViewModels;

/// <summary>One item tile inside a trade offer's detail view: name (localized fallback when Steam gave
/// none, shown as a tooltip), a stack-count badge, and its Steam economy image (loaded lazily).</summary>
public sealed partial class TradeLine : ObservableObject
{
    public string Name { get; }
    public int Amount { get; }
    public int AppId { get; }
    public bool HasCount => Amount > 1;
    public string CountText => Amount.ToString();
    /// <summary>Full CDN url for the item image, or null when Steam sent no icon.</summary>
    public string? IconUrl { get; }
    [ObservableProperty] private Bitmap? _image;
    /// <summary>The owning game's name, filled in asynchronously; null until resolved.</summary>
    [ObservableProperty] private string? _gameName;

    /// <summary>Item name plus the owning game — always shows the appid so a look-alike can't hide it.</summary>
    public string Tooltip => GameName is { Length: > 0 } g
        ? $"{Name}\n{g} · {AppId}"
        : $"{Name}\nApp {AppId}";

    public TradeLine(TradeItem item)
    {
        Name = item.Name.Length > 0 ? item.Name : Loc.T("Offers_UnknownItem");
        Amount = item.Amount;
        AppId = item.AppId;
        IconUrl = item.IconUrl.Length > 0
            ? $"https://community.cloudflare.steamstatic.com/economy/image/{item.IconUrl}/96fx96f"
            : null;
    }

    partial void OnGameNameChanged(string? value) => OnPropertyChanged(nameof(Tooltip));
}

/// <summary>A trade offer row: the offer plus display helpers and its accept/decline affordances.
/// Clicking the row opens a detail dialog built from these item lines.</summary>
public sealed partial class OfferItem : ObservableObject
{
    public TradeOffer Offer { get; }
    public OfferItem(TradeOffer offer)
    {
        Offer = offer;
        GiveLines = offer.GiveItems.Select(i => new TradeLine(i)).ToList();
        ReceiveLines = offer.ReceiveItems.Select(i => new TradeLine(i)).ToList();
    }

    public IReadOnlyList<TradeLine> GiveLines { get; }
    public IReadOnlyList<TradeLine> ReceiveLines { get; }
    public bool HasGive => GiveLines.Count > 0;
    public bool HasReceive => ReceiveLines.Count > 0;

    /// <summary>The trade partner's 64-bit SteamID and profile (resolved asynchronously when the dialog opens).</summary>
    public string PartnerSteamId64 => Offer.PartnerSteamId.ToString();
    [ObservableProperty] private string? _partnerName;
    public bool HasPartnerName => !string.IsNullOrEmpty(PartnerName);
    partial void OnPartnerNameChanged(string? value) => OnPropertyChanged(nameof(HasPartnerName));

    [ObservableProperty] private Bitmap? _partnerAvatar;
    [ObservableProperty] private int _partnerLevel;
    public bool HasLevel => PartnerLevel > 0;
    partial void OnPartnerLevelChanged(int value) => OnPropertyChanged(nameof(HasLevel));
    [ObservableProperty] private string _memberSince = "";
    public bool HasMemberSince => MemberSince.Length > 0;
    partial void OnMemberSinceChanged(string value) => OnPropertyChanged(nameof(HasMemberSince));
    [ObservableProperty] private string _friendText = "";
    public bool HasFriendText => FriendText.Length > 0;
    partial void OnFriendTextChanged(string value) => OnPropertyChanged(nameof(HasFriendText));
    /// <summary>Whether the partner is a friend (drives the chip's green highlight).</summary>
    [ObservableProperty] private bool _isPartnerFriend;
    /// <summary>True once the partner lookups have run (drives the info row's visibility).</summary>
    [ObservableProperty] private bool _partnerInfoLoaded;

    public bool IsIncoming => Offer.IsIncoming;
    public string Direction => Offer.IsIncoming ? Loc.T("Offers_Incoming") : Loc.T("Offers_Outgoing");
    /// <summary>Decline (reject an incoming offer) vs. Cancel (retract one we sent).</summary>
    public string DeclineLabel => Offer.IsIncoming ? Loc.T("Offers_Decline") : Loc.T("Offers_Cancel");
    public string Summary => Loc.T("Offers_Summary", Offer.GiveCount, Offer.ReceiveCount);
    public string Partner => $"#{Offer.PartnerAccountId}";
    public string Message => Offer.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Offer.Message);
    /// <summary>The offer message, or a localized "no message" placeholder so the field is always shown.</summary>
    public string MessageOrNone => HasMessage ? Offer.Message : Loc.T("Offers_NoMessage");
}

/// <summary>A notification row: type label + read state + relative time. Read is mutable so a
/// "mark read" flips the dot without a re-fetch.</summary>
public sealed partial class NotificationItem : ObservableObject
{
    public SteamNotification N { get; }
    public NotificationItem(SteamNotification n) { N = n; _read = n.Read; }

    public string Id => N.Id;
    public ulong SenderId => N.SenderId;
    public string? Url => N.Url;
    public bool HasUrl => !string.IsNullOrEmpty(N.Url);

    [ObservableProperty] private bool _read;
    public bool Unread => !Read;
    partial void OnReadChanged(bool value) => OnPropertyChanged(nameof(Unread));

    // Persona of the other party, resolved lazily from SenderId after the list loads.
    [ObservableProperty] private string? _persona;
    partial void OnPersonaChanged(string? value) => OnPropertyChanged(nameof(Subtitle));

    /// <summary>Localized kind label: "Comment", "Trade offer", …</summary>
    public string Title => N.Kind switch
    {
        "tradeoffer" => Loc.T("Notif_KindTradeOffer"),
        "comment" => Loc.T("Notif_KindComment"),
        _ => Loc.T("Notif_KindOther"),
    };

    public string When => N.Timestamp <= 0
        ? ""
        : DateTimeOffset.FromUnixTimeSeconds(N.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>"Persona · time", or just the time until the persona resolves.</summary>
    public string Subtitle => string.IsNullOrEmpty(Persona) ? When : $"{Persona} · {When}";
}

/// <summary>
/// The two extra detail lists — trade offers (accept/decline) and Steam notifications — plus the tab
/// selector that shows one of confirmations / offers / notifications. Both are read via the account's
/// access token; a failed fetch asks the user to sign in again.
/// </summary>
public partial class MainViewModel
{
    private readonly SteamTradeOffersService _offersService = new();
    private readonly SteamNotificationsService _notifService = new();

    // 0 = confirmations, 1 = trade offers, 2 = notifications, 3 = devices
    [ObservableProperty] private int _detailTab;
    public bool IsConfTab => DetailTab == 0;
    public bool IsOffersTab => DetailTab == 1;
    public bool IsNotifTab => DetailTab == 2;
    public bool IsDevicesTab => DetailTab == 3;

    public ObservableCollection<OfferItem> TradeOffers { get; } = new();
    public ObservableCollection<NotificationItem> Notifications { get; } = new();

    [ObservableProperty] private bool _offersBusy;
    [ObservableProperty] private string _offersStatus = "";
    [ObservableProperty] private bool _notificationsBusy;
    [ObservableProperty] private string _notificationsStatus = "";

    /// <summary>True while any of the four tabs is loading — drives the single Refresh button's spinner.</summary>
    public bool AnyTabBusy => ConfirmationsBusy || OffersBusy || NotificationsBusy || DevicesBusy;
    partial void OnOffersBusyChanged(bool value) => OnPropertyChanged(nameof(AnyTabBusy));
    partial void OnNotificationsBusyChanged(bool value) => OnPropertyChanged(nameof(AnyTabBusy));
    partial void OnConfirmationsBusyChanged(bool value) => OnPropertyChanged(nameof(AnyTabBusy));
    partial void OnDevicesBusyChanged(bool value) => OnPropertyChanged(nameof(AnyTabBusy));

    private bool _offersLoaded, _notifLoaded;

    partial void OnDetailTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsConfTab));
        OnPropertyChanged(nameof(IsOffersTab));
        OnPropertyChanged(nameof(IsNotifTab));
        OnPropertyChanged(nameof(IsDevicesTab));
        EnsureTabLoaded();
    }

    [RelayCommand]
    private void SelectTab(object? tab)
        => DetailTab = tab is string s && int.TryParse(s, out var t) ? t : 0;

    /// <summary>
    /// The account changed: clear both extra lists and refresh them right away — not lazily on tab
    /// open — so the Trade offers / Notifications badge counts are correct the moment you switch.
    /// (Confirmations refresh on switch via LoadConfirmations.) Marking them loaded keeps a later tab
    /// click from re-fetching.
    /// </summary>
    private void ResetDetailTabs()
    {
        TradeOffers.Clear();
        Notifications.Clear();
        Devices.Clear();
        OffersStatus = NotificationsStatus = DevicesStatus = "";
        OnPropertyChanged(nameof(OffersCount));
        NotifyDevicesChanged();
        NotifyUnreadChanged();
        _offersLoaded = _notifLoaded = true;
        DevicesLoaded = true; // load all four up front so every tab's badge is live
        _ = RefreshOffers();
        _ = RefreshNotifications();
        _ = RefreshDevices();
    }

    private void EnsureTabLoaded()
    {
        if (DetailTab == 1 && !_offersLoaded) { _offersLoaded = true; _ = RefreshOffers(); }
        if (DetailTab == 2 && !_notifLoaded) { _notifLoaded = true; _ = RefreshNotifications(); }
        // Devices load lazily (they need a token renew + a network round-trip) — only on first open.
        if (DetailTab == 3 && !DevicesLoaded) { DevicesLoaded = true; _ = RefreshDevices(); }
    }

    public int OffersCount => TradeOffers.Count;
    public int NotificationsUnread
    {
        get { int c = 0; foreach (var n in Notifications) if (n.Unread) c++; return c; }
    }
    public bool HasUnreadNotifications => NotificationsUnread > 0;
    public bool NotificationsHasAny => Notifications.Count > 0;

    /// <summary>Raise after any change to notifications' read state so the count, the "any unread?" flag
    /// (Mark-all-read button) and the "any at all?" flag (badge) refresh together.</summary>
    private void NotifyUnreadChanged()
    {
        OnPropertyChanged(nameof(NotificationsUnread));
        OnPropertyChanged(nameof(HasUnreadNotifications));
        OnPropertyChanged(nameof(NotificationsHasAny));
    }

    // ---- trade offers ----

    [RelayCommand]
    private async Task RefreshOffers()
    {
        if (SelectedAccount is not { IsReal: true, HasSession: true } acc || OffersBusy)
        {
            if (SelectedAccount is not { HasSession: true }) OffersStatus = Loc.T("Offers_SignIn");
            return;
        }
        OffersBusy = true;
        OffersStatus = Loc.T("Offers_Loading");
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
            {
                OffersStatus = Loc.T("Offers_SessionExpired");
                return;
            }
            var offers = await _offersService.FetchAsync(token);
            if (!ReferenceEquals(SelectedAccount, acc)) return;
            TradeOffers.Clear();
            foreach (var o in offers) TradeOffers.Add(new OfferItem(o));
            OnPropertyChanged(nameof(OffersCount));
            OffersStatus = offers.Count == 0 ? Loc.T("Offers_None") : "";
        }
        catch (Exception)
        {
            OffersStatus = Loc.T("Offers_LoadFailed");
        }
        finally { OffersBusy = false; }
    }

    /// <summary>The offer shown in the detail dialog.</summary>
    [ObservableProperty] private OfferItem? _detailOffer;

    // appid -> game name, resolved once via the Steam store and reused across offers/accounts.
    private static readonly Dictionary<int, string> _appNameCache = new();

    /// <summary>Opens the in-app detail dialog for an offer and lazily loads item images + game names.</summary>
    [RelayCommand]
    private async Task OpenOfferDetails(OfferItem item)
    {
        if (item is null) return;
        CloseDialogs();
        DetailOffer = item;
        ShowOfferDetails = true;
        await LoadOfferDetails(item);
    }

    /// <summary>Fills each item's Steam economy image and owning-game name into its TradeLine.</summary>
    private async Task LoadOfferDetails(OfferItem offer)
    {
        var lines = offer.ReceiveLines.Concat(offer.GiveLines).ToList();

        foreach (var line in lines)
        {
            if (line.IconUrl is null || line.Image is not null) continue;
            try
            {
                var bytes = await _steam.DownloadAsync(line.IconUrl);
                if (bytes is not null) line.Image = new Bitmap(new MemoryStream(bytes));
            }
            catch { /* leave the placeholder */ }
        }

        foreach (var appId in lines.Select(l => l.AppId).Where(a => a > 0).Distinct())
        {
            var name = await ResolveAppNameAsync(appId);
            if (name.Length == 0) continue;
            foreach (var line in lines.Where(l => l.AppId == appId)) line.GameName = name;
        }

        if (!offer.PartnerInfoLoaded && offer.Offer.PartnerSteamId > 0
            && SelectedAccount is { } acc && ulong.TryParse(acc.SteamId, out var selfId)
            && await EnsureFreshSessionAsync(acc) && acc.AccessToken is { } token)
        {
            try
            {
                // Persona + avatar from the public community XML — reliable and key-free.
                var profile = await _steam.FetchProfileAsync(offer.Offer.PartnerSteamId);
                if (profile is { PersonaName.Length: > 0 }) offer.PartnerName = profile.PersonaName;
                if (profile is { AvatarUrl.Length: > 0 })
                {
                    var av = await _steam.DownloadAsync(profile.AvatarUrl);
                    if (av is not null) offer.PartnerAvatar = new Bitmap(new MemoryStream(av));
                }

                // Level / member-since / friendship scraped from the offer page (token APIs 400 for these).
                var page = SteamTradeOffersService.ParseTradePartnerPage(
                    await _offersService.FetchOfferPageAsync(offer.Offer.Id, selfId, token));
                if (page.Level > 0) offer.PartnerLevel = page.Level;
                offer.MemberSince = page.MemberSince;
                offer.IsPartnerFriend = page.IsFriend;
                offer.FriendText = page.IsFriend
                    ? (page.FriendFor.Length > 0 ? Loc.T("Offers_FriendFor", page.FriendFor) : Loc.T("Offers_Friend"))
                    : Loc.T("Offers_NotFriend");
            }
            catch { /* leave the 64-bit id showing on its own */ }
            finally { offer.PartnerInfoLoaded = true; }
        }
    }

    /// <summary>Looks up a game's name for an appid (cached), via the store's basic appdetails.</summary>
    private async Task<string> ResolveAppNameAsync(int appId)
    {
        if (_appNameCache.TryGetValue(appId, out var cached)) return cached;
        try
        {
            var bytes = await _steam.DownloadAsync(
                $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic");
            if (bytes is not null)
            {
                var name = SteamTradeOffersService.ParseAppName(System.Text.Encoding.UTF8.GetString(bytes), appId);
                if (name.Length > 0) { _appNameCache[appId] = name; return name; }
            }
        }
        catch { /* fall back to the appid shown in the tooltip */ }
        return "";
    }

    /// <summary>Opens the offer on Steam (from the detail dialog) for images/escrow in the browser.</summary>
    [RelayCommand]
    private async Task OpenOffer(OfferItem item)
    {
        if (item is null || OpenUrl is null) return;
        await OpenUrl($"https://steamcommunity.com/tradeoffer/{item.Offer.Id}/");
    }

    [RelayCommand]
    private Task AcceptOffer(OfferItem item) => RespondOffer(item, accept: true);

    [RelayCommand]
    private Task DeclineOffer(OfferItem item) => RespondOffer(item, accept: false);

    private async Task RespondOffer(OfferItem item, bool accept)
    {
        if (SelectedAccount is not { } acc || !ulong.TryParse(acc.SteamId, out var steamId) || OffersBusy) return;
        OffersBusy = true;
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
            {
                OffersStatus = Loc.T("Offers_SessionExpired");
                return;
            }
            var result = accept
                ? await _offersService.AcceptAsync(steamId, token, item.Offer)
                : await _offersService.DeclineAsync(steamId, token, item.Offer);
            if (!result.Ok)
            {
                OffersStatus = StatusLine.Error(result.Error ?? "");
                return;
            }
            if (accept && result.NeedsMobileConfirmation)
                await ConfirmTradeAsync(acc, item.Offer.Id);
            TradeOffers.Remove(item);
            OnPropertyChanged(nameof(OffersCount));
            OffersStatus = "";
            // If this offer was open in the detail dialog, close it now that it's gone.
            if (ShowOfferDetails && ReferenceEquals(DetailOffer, item)) CloseDialogs();
        }
        catch (Exception ex) { OffersStatus = StatusLine.Error(ex); }
        finally { OffersBusy = false; }
    }

    // ---- notifications ----

    [RelayCommand]
    private async Task RefreshNotifications()
    {
        if (SelectedAccount is not { IsReal: true, HasSession: true } acc || NotificationsBusy)
        {
            if (SelectedAccount is not { HasSession: true }) NotificationsStatus = Loc.T("Offers_SignIn");
            return;
        }
        NotificationsBusy = true;
        NotificationsStatus = Loc.T("Offers_Loading");
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
            {
                NotificationsStatus = Loc.T("Offers_SessionExpired");
                return;
            }
            var items = await _notifService.FetchAsync(token);
            if (!ReferenceEquals(SelectedAccount, acc)) return;
            Notifications.Clear();
            foreach (var n in items) Notifications.Add(new NotificationItem(n));
            NotifyUnreadChanged();
            NotificationsStatus = Notifications.Count == 0 ? Loc.T("Notif_None") : "";
            _ = ResolveNotificationPersonasAsync(Notifications.ToList()); // fill in "who" in the background
        }
        catch (Exception)
        {
            NotificationsStatus = Loc.T("Offers_LoadFailed");
        }
        finally { NotificationsBusy = false; }
    }

    [RelayCommand]
    private async Task MarkNotificationRead(NotificationItem item)
    {
        if (item is null || item.Read || SelectedAccount is not { } acc) return;
        if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token) return;
        if (await _notifService.MarkReadAsync(token, new[] { item.Id }))
        {
            item.Read = true;
            NotifyUnreadChanged();
        }
    }

    /// <summary>Opens a notification's target page (trade offer / commenter profile) in the browser.</summary>
    [RelayCommand]
    private async Task OpenNotification(NotificationItem? item)
    {
        if (item?.Url is { Length: > 0 } url && OpenUrl is not null) await OpenUrl(url);
    }

    /// <summary>Resolves each notification's sender persona (cached), so rows show "who" not a bare time.</summary>
    private async Task ResolveNotificationPersonasAsync(IReadOnlyList<NotificationItem> items)
    {
        var seen = new Dictionary<ulong, string?>();
        foreach (var item in items)
        {
            var id = item.SenderId;
            if (id == 0) continue;
            if (!seen.TryGetValue(id, out var persona))
            {
                persona = _profileCache.Load(id).Profile?.PersonaName;
                if (string.IsNullOrEmpty(persona))
                {
                    try { persona = (await _steam.FetchProfileAsync(id))?.PersonaName; } catch { persona = null; }
                }
                seen[id] = persona;
            }
            if (!string.IsNullOrEmpty(persona)) item.Persona = persona;
        }
    }

    [RelayCommand]
    private async Task MarkAllNotificationsRead()
    {
        if (SelectedAccount is not { } acc || NotificationsBusy || Notifications.Count == 0) return;
        if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (await _notifService.MarkAllReadAsync(token, now))
        {
            foreach (var n in Notifications) n.Read = true;
            NotifyUnreadChanged();
        }
    }
}
