using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.Core.Steam;
using Vouch.Core.Storage;
using Vouch.App.Localization;

namespace Vouch.App.ViewModels;

/// <summary>"Update info": live persona name, avatar, and VAC/game/trade-ban refresh.</summary>
public partial class MainViewModel
{
    private readonly SteamWebClient _steam = new();

    [ObservableProperty] private string _updateStatus = "";

    [RelayCommand]
    private async Task UpdateInfo()
    {
        if (SelectedAccount is not { } acc || IsUpdatingInfo) return;
        IsUpdatingInfo = true;
        UpdateStatus = "";
        try { await UpdateInfoForAsync(acc); }
        catch (Exception ex)
        {
            UpdateStatus = ex is InvalidOperationException
                ? ex.Message // e.g. private profile
                : Loc.T("Profile_StatusUpdateFailed", ex.Message);
        }
        finally { IsUpdatingInfo = false; }
    }

    /// <summary>Refreshes several accounts' persona/avatar/bans at once (from the sidebar selection).</summary>
    public async Task BatchUpdateInfo(System.Collections.Generic.IReadOnlyList<AccountViewModel> accounts)
    {
        int ok = 0, failed = 0;
        foreach (var acc in accounts)
        {
            try { await UpdateInfoForAsync(acc); ok++; }
            catch (Exception) { failed++; }
        }
        ShowToast(Loc.T(failed == 0 ? "Batch_UpdateDone" : "Batch_UpdateMixed", ok, failed),
                  failed == 0 ? ToastKind.Success : ToastKind.Error);
    }

    /// <summary>Fetches and applies one account's live profile/avatar/bans. Throws on failure (so both
    /// the single command and the batch loop can report it); demo accounts get the mock info.</summary>
    private async Task UpdateInfoForAsync(AccountViewModel acc)
    {
        // Demo accounts have fake SteamIDs — keep the mock behavior for them.
        if (!acc.IsReal || !ulong.TryParse(acc.SteamId, out var steamId))
        {
            await Task.Delay(700);
            acc.ApplyFetchedInfo();
            return;
        }

        var profile = await _steam.FetchProfileAsync(steamId)
            ?? throw new InvalidOperationException(Loc.T("Profile_StatusLoadFailed"));

        byte[]? avatarBytes = null;
        Avalonia.Media.Imaging.Bitmap? avatar = null;
        if (!string.IsNullOrEmpty(profile.AvatarUrl))
        {
            avatarBytes = await _steam.DownloadAsync(profile.AvatarUrl);
            if (avatarBytes is not null) avatar = new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(avatarBytes));
        }

        acc.ApplyProfile(profile.PersonaName, avatar, profile.VacBanned, profile.TradeBanned);

        // The community XML omits GAME bans. With a Web API key, GetPlayerBans gives exact counts;
        // without one, scrape the public profile's ban banner so game bans still surface.
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            var bans = await _steam.FetchBansAsync(steamId, ApiKey.Trim());
            if (bans is not null) acc.ApplyBans(bans.VacBans, bans.GameBans);
        }
        else
        {
            var status = await _steam.FetchBanStatusAsync(steamId);
            if (status is not null)
                acc.ApplyBans(Math.Max(acc.VacBans, status.VacBans), status.GameBans);
        }

        // Persist so the persona/avatar/bans survive a restart.
        _profileCache.Save(steamId, new CachedProfile
        {
            PersonaName = acc.PersonaName,
            VacBans = acc.VacBans,
            GameBans = acc.GameBans,
            TradeBanned = acc.TradeBanned,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        }, avatarBytes);

        if (ReferenceEquals(acc, SelectedAccount)) OnPropertyChanged(nameof(Username));
    }
}
