using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Steam;

namespace Vouch.App.ViewModels;

/// <summary>A game whose inventory can be transferred — a preset (appid + contextid) with a checkbox.</summary>
public sealed partial class TransferGame : ObservableObject
{
    public int AppId { get; }
    public string ContextId { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;

    public TransferGame(int appId, string contextId, string name)
    {
        AppId = appId;
        ContextId = contextId;
        Name = name;
    }
}

/// <summary>A single tradable item in the item-picker list, with its checkbox state and icon.</summary>
public sealed partial class TransferItem : ObservableObject
{
    public InventoryItem Model { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _image;
    public string Name => Model.Name;
    public string? IconUrl => Model.IconUrl.Length > 0
        ? $"https://community.cloudflare.steamstatic.com/economy/image/{Model.IconUrl}/96fx96f"
        : null;

    public TransferItem(InventoryItem model) => Model = model;
}

/// <summary>
/// Inventory transfer: for each selected game, sends that account's whole tradable inventory as one
/// trade offer to the configured trade link, then confirms it on mobile. One offer per game; only
/// currently-tradable items. Sends from the current account or a multi-selection.
/// </summary>
public partial class MainViewModel
{
    private readonly SteamInventoryClient _inventory = new();
    private readonly SteamTradeService _trade = new();
    private List<AccountViewModel> _transferTargets = new();

    /// <summary>Common games that hold tradable inventory (appid, contextid). The user ticks which to send.</summary>
    public ObservableCollection<TransferGame> TransferGames { get; } = new()
    {
        new(730, "2", "CS2 / CS:GO"),
        new(440, "2", "Team Fortress 2"),
        new(570, "2", "Dota 2"),
        new(252490, "2", "Rust"),
        new(753, "6", "Steam (cards, backgrounds…)"),
    };

    [ObservableProperty] private bool _transferBusy;
    [ObservableProperty] private bool _useCustomAppId;          // opt in to a custom appid via checkbox
    [ObservableProperty] private string _customAppId = "";      // optional extra game to include
    [ObservableProperty] private string _customContextId = "2";

    // Item-picker ("select items") mode.
    [ObservableProperty] private bool _transferSelectMode;
    [ObservableProperty] private TransferGame? _selectGame;
    [ObservableProperty] private bool _loadingInventory;
    [ObservableProperty] private bool _inventoryLoaded;
    public ObservableCollection<TransferItem> InventoryItems { get; } = new();

    /// <summary>True once an inventory load finished and came back empty — drives the "no items" placeholder.</summary>
    public bool HasNoInventory => InventoryLoaded && !LoadingInventory && InventoryItems.Count == 0;
    partial void OnLoadingInventoryChanged(bool value) => OnPropertyChanged(nameof(HasNoInventory));
    partial void OnInventoryLoadedChanged(bool value) => OnPropertyChanged(nameof(HasNoInventory));

    // Auto-refresh the item list when the category changes (game picked, or custom-appid toggled).
    partial void OnSelectGameChanged(TransferGame? value)
    {
        if (TransferSelectMode && !UseCustomAppId) _ = LoadInventory();
    }
    partial void OnTransferSelectModeChanged(bool value)
    {
        if (value) _ = LoadInventory();
    }
    partial void OnUseCustomAppIdChanged(bool value)
    {
        if (TransferSelectMode) _ = LoadInventory();
    }

    /// <summary>Whether a target trade link is configured (parses OK). Drives the dialog's warning/enable.</summary>
    public bool HasTradeUrl => SteamTradeService.ParseTradeUrl(TradeUrl) is not null;

    /// <summary>The game to act on: the custom appid if filled, else the picked preset.</summary>
    private (int App, string Ctx, string Name)? EffectiveGame()
    {
        if (UseCustomAppId && int.TryParse(CustomAppId.Trim(), out var app) && app > 0)
            return (app, string.IsNullOrWhiteSpace(CustomContextId) ? "2" : CustomContextId.Trim(), $"App {app}");
        return SelectGame is { } g ? (g.AppId, g.ContextId, g.Name) : null;
    }

    [RelayCommand]
    private void OpenTransfer() => BeginTransfer(SelectedAccount is { } a ? new[] { a } : Array.Empty<AccountViewModel>());

    [RelayCommand] private void TransferModeAll() => TransferSelectMode = false;
    [RelayCommand] private void TransferModePick() => TransferSelectMode = true;

    /// <summary>Opens the transfer dialog for the given accounts (from the sidebar batch action).</summary>
    public void BeginTransfer(IReadOnlyList<AccountViewModel> accounts)
    {
        _transferTargets = accounts.Where(a => a.IsReal).ToList();
        if (_transferTargets.Count == 0) return;
        CloseDialogs();
        TransferBusy = false;
        TransferGames.Clear();          // don't flash the presets — show "Loading…" until the real list arrives
        TransferGamesLoading = true;
        InventoryItems.Clear();
        InventoryLoaded = false;
        OnPropertyChanged(nameof(HasNoInventory));
        ShowTransfer = true;
        _ = RefreshTransferGamesAsync(); // fill with the account's actual games-with-items
    }

    [ObservableProperty] private bool _transferGamesLoading;

    // The static fallback list, used only when the account's real games-with-items can't be read.
    private static readonly (int App, string Ctx, string Name)[] PresetGames =
    {
        (730, "2", "CS2 / CS:GO"), (440, "2", "Team Fortress 2"), (570, "2", "Dota 2"),
        (252490, "2", "Rust"), (753, "6", "Steam (cards, backgrounds…)"),
    };

    /// <summary>Rebuilds <see cref="TransferGames"/> from the games the transfer target(s) actually hold
    /// items in (union across a multi-account selection), with counts — same source as the inventory
    /// viewer. Falls back to <see cref="PresetGames"/> if nothing could be read (offline / not signed in).</summary>
    private async Task RefreshTransferGamesAsync()
    {
        TransferGamesLoading = true;
        try
        {
            var union = new Dictionary<int, (string Ctx, string Name, int Count)>();
            foreach (var acc in _transferTargets.Where(a => a.IsReal).ToList())
            {
                try
                {
                    if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token
                        || !ulong.TryParse(acc.SteamId, out var sid))
                        continue;
                    foreach (var app in await _inventory.FetchInventoryAppsAsync(sid, token))
                        union[app.AppId] = union.TryGetValue(app.AppId, out var cur)
                            ? (cur.Ctx, cur.Name, cur.Count + app.AssetCount)
                            : (app.ContextId, app.Name, app.AssetCount);
                }
                catch { /* skip this account */ }
            }
            if (!ShowTransfer) return; // dialog was closed while fetching

            var ticked = TransferGames.Where(g => g.IsSelected).Select(g => g.AppId).ToHashSet();
            TransferGames.Clear();
            if (union.Count > 0)
                foreach (var kv in union.OrderByDescending(k => k.Value.Count))
                    TransferGames.Add(new TransferGame(kv.Key, kv.Value.Ctx, $"{kv.Value.Name} ({kv.Value.Count})")
                    { IsSelected = ticked.Contains(kv.Key) });
            else
                foreach (var p in PresetGames)
                    TransferGames.Add(new TransferGame(p.App, p.Ctx, p.Name));

            SelectGame = TransferGames.FirstOrDefault();
        }
        finally { TransferGamesLoading = false; }
    }

    /// <summary>Loads the chosen game's tradable items (with names) for the first signed-in target, so
    /// the user can pick which to send. Select-items mode is single-account.</summary>
    [RelayCommand]
    private async Task LoadInventory()
    {
        if (LoadingInventory) return;
        var acc = _transferTargets.FirstOrDefault(a => a is { IsReal: true, HasSession: true });
        if (acc is null || !ulong.TryParse(acc.SteamId, out var steamId) || EffectiveGame() is not { } game)
        {
            ShowToast(Loc.T("Transfer_NoSignedIn"), ToastKind.Error);
            return;
        }

        LoadingInventory = true;
        InventoryLoaded = false;
        InventoryItems.Clear();
        OnPropertyChanged(nameof(HasNoInventory));
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
            {
                ShowToast(Loc.T("Transfer_NoSignedIn"), ToastKind.Error);
                return;
            }
            var items = await _inventory.FetchTradableItemsAsync(steamId, game.App, game.Ctx, token);
            foreach (var it in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                InventoryItems.Add(new TransferItem(it));
            InventoryLoaded = true;
            OnPropertyChanged(nameof(HasNoInventory));
            _ = LoadItemImagesAsync(InventoryItems.ToList()); // fill icons in the background
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T("Transfer_LoadError", ex.Message), ToastKind.Error);
        }
        finally { LoadingInventory = false; }
    }

    /// <summary>Downloads each picker item's Steam economy image (once) in the background.</summary>
    private async Task LoadItemImagesAsync(List<TransferItem> items)
    {
        foreach (var item in items)
        {
            if (item.IconUrl is null || item.Image is not null) continue;
            try
            {
                var bytes = await _steam.DownloadAsync(item.IconUrl);
                if (bytes is not null)
                    item.Image = new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(bytes));
            }
            catch { /* leave the placeholder */ }
        }
    }

    [RelayCommand]
    private void ToggleSelectAllItems()
    {
        bool selectAll = InventoryItems.Any(i => !i.IsSelected); // if any unselected, select all; else clear
        foreach (var i in InventoryItems) i.IsSelected = selectAll;
    }

    [RelayCommand]
    private async Task DoTransfer()
    {
        if (TransferBusy) return;
        if (SteamTradeService.ParseTradeUrl(TradeUrl) is not { } target)
        {
            ShowToast(Loc.T("Transfer_NoTradeUrl"), ToastKind.Error);
            return;
        }
        if (TransferSelectMode) { await DoTransferSelected(target); return; }

        var games = TransferGames.Where(g => g.IsSelected).ToList();
        if (UseCustomAppId && int.TryParse(CustomAppId.Trim(), out var customApp) && customApp > 0)
        {
            var ctx = string.IsNullOrWhiteSpace(CustomContextId) ? "2" : CustomContextId.Trim();
            games.Add(new TransferGame(customApp, ctx, $"App {customApp} · ctx {ctx}"));
        }
        if (games.Count == 0)
        {
            ShowToast(Loc.T("Transfer_NoGames"), ToastKind.Error);
            return;
        }

        TransferBusy = true;
        int offers = 0, itemCount = 0, failed = 0;
        try
        {
            foreach (var acc in _transferTargets)
            {
                if (acc is not { IsReal: true, HasSession: true } || !ulong.TryParse(acc.SteamId, out var steamId))
                    continue; // not signed in — skip silently
                if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
                {
                    failed++;
                    continue;
                }

                foreach (var g in games)
                {
                    try
                    {
                        var tradable = await _inventory.FetchTradableAsync(steamId, g.AppId, g.ContextId, token);
                        if (tradable.Count == 0) continue;

                        var assets = tradable
                            .Select(t => new TradeAsset(t.AppId, t.ContextId, t.AssetId, t.Amount))
                            .ToList();
                        var result = await _trade.SendOfferAsync(steamId, token, target, assets);
                        if (!result.Ok) { failed++; continue; }

                        offers++;
                        itemCount += assets.Count;
                        if (result.NeedsMobileConfirmation && result.TradeOfferId is { } id)
                            await ConfirmTradeAsync(acc, id);
                    }
                    catch { failed++; }
                }
            }

            // Every outcome is summarized by the toast; the dialog shows no running log.
            ShowToast(Loc.T("Transfer_Done", offers, itemCount, failed), failed == 0 ? ToastKind.Success : ToastKind.Error);
        }
        finally { TransferBusy = false; }
    }

    /// <summary>Select-items mode: sends the ticked items (one offer) from the first signed-in target.</summary>
    private async Task DoTransferSelected(TradeTarget target)
    {
        var acc = _transferTargets.FirstOrDefault(a => a is { IsReal: true, HasSession: true });
        if (acc is null || !ulong.TryParse(acc.SteamId, out var steamId))
        {
            ShowToast(Loc.T("Transfer_NoSignedIn"), ToastKind.Error);
            return;
        }
        var picked = InventoryItems.Where(i => i.IsSelected).Select(i => i.Model).ToList();
        if (picked.Count == 0)
        {
            ShowToast(Loc.T("Transfer_NoItems"), ToastKind.Error);
            return;
        }

        TransferBusy = true;
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
            {
                ShowToast(Loc.T("Transfer_NoSignedIn"), ToastKind.Error);
                return;
            }

            var assets = picked.Select(i => new TradeAsset(i.AppId, i.ContextId, i.AssetId, i.Amount)).ToList();
            var result = await _trade.SendOfferAsync(steamId, token, target, assets);
            if (!result.Ok)
            {
                ShowToast(result.Error ?? Loc.T("Transfer_Done", 0, 0, 1), ToastKind.Error);
                return;
            }

            if (result.NeedsMobileConfirmation && result.TradeOfferId is { } id)
                await ConfirmTradeAsync(acc, id);

            ShowToast(Loc.T("Transfer_Done", 1, assets.Count, 0), ToastKind.Success);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, ToastKind.Error);
        }
        finally { TransferBusy = false; }
    }

    /// <summary>Accepts the mobile confirmation Steam raises for a just-sent trade offer (matched by id).</summary>
    private async Task<bool> ConfirmTradeAsync(AccountViewModel acc, string tradeOfferId)
    {
        if (CredentialsFor(acc) is not { } creds) return false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var confs = await _confirmService.FetchAsync(creds);
                if (confs.FirstOrDefault(c => c.CreatorId == tradeOfferId) is { } match)
                    return await _confirmService.RespondAsync(creds, match, accept: true);
            }
            catch (Exception) { /* retry */ }
            await Task.Delay(1500); // the confirmation can take a moment to appear
        }
        return false;
    }
}
