using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Steam;

namespace Vouch.App.ViewModels;

/// <summary>
/// In-app inventory viewer. Like Steam, only lists the games the account actually holds items in (read
/// from the inventory page's g_rgAppContextData), then shows the picked game's items as an icon grid.
/// Read-only; reuses <see cref="_inventory"/> + the transfer flow's item tiles and image loader.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<InventoryApp> InventoryApps { get; } = new();
    [ObservableProperty] private InventoryApp? _selectedInventoryApp;
    [ObservableProperty] private bool _inventoryBusy;
    [ObservableProperty] private string _inventoryStatus = "";
    public bool InventoryHasApps => InventoryApps.Count > 0;

    // Custom-appid escape hatch: view a game that isn't in the "games with items" list.
    [ObservableProperty] private bool _invUseCustom;
    [ObservableProperty] private string _invCustomAppId = "";
    [ObservableProperty] private string _invCustomContextId = "2";

    private AccountViewModel? _inventoryAcc;
    private bool _settingInventoryApps; // don't react to SelectedInventoryApp while (re)building the list
    private int _invLoadSeq;            // guards against out-of-order item loads (dropdown vs custom)

    /// <summary>Opens the viewer for the selected account and loads its games-with-items.</summary>
    [RelayCommand]
    private async Task OpenInventory()
    {
        if (SelectedAccount is not { IsReal: true } acc) return;
        _inventoryAcc = acc;
        CloseDialogs();
        InventoryApps.Clear();
        InventoryItems.Clear();
        OnPropertyChanged(nameof(InventoryHasApps));
        InvUseCustom = false;
        InventoryStatus = "";
        ShowInventory = true;
        await LoadInventoryAppsAsync();
    }

    private async Task LoadInventoryAppsAsync()
    {
        if (_inventoryAcc is not { } acc) return;
        InventoryBusy = true;
        InventoryStatus = Loc.T("Inv_Loading");
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token
                || !ulong.TryParse(acc.SteamId, out var steamId))
            {
                InventoryStatus = Loc.T("Inv_SignIn");
                return;
            }
            var apps = await _inventory.FetchInventoryAppsAsync(steamId, token);
            if (!ReferenceEquals(_inventoryAcc, acc) || !ShowInventory) return; // account switched / closed

            _settingInventoryApps = true;
            InventoryApps.Clear();
            foreach (var a in apps) InventoryApps.Add(a);
            OnPropertyChanged(nameof(InventoryHasApps));
            SelectedInventoryApp = InventoryApps.FirstOrDefault();
            _settingInventoryApps = false;

            if (SelectedInventoryApp is { } first) await LoadInventoryItemsAsync(first);
            else InventoryStatus = Loc.T("Inv_Empty");
        }
        catch (Exception ex) { InventoryStatus = StatusLine.Error(ex); }
        finally { InventoryBusy = false; }
    }

    partial void OnSelectedInventoryAppChanged(InventoryApp? value)
    {
        if (_settingInventoryApps || value is null) return;
        _ = LoadInventoryItemsAsync(value);
    }

    /// <summary>Re-load the picked game's items when leaving custom mode.</summary>
    partial void OnInvUseCustomChanged(bool value)
    {
        if (!value && SelectedInventoryApp is { } app) _ = LoadInventoryItemsAsync(app);
    }

    /// <summary>Loads a hand-typed appid/context (for games not in the "games with items" list).</summary>
    [RelayCommand]
    private async Task LoadCustomInventory()
    {
        if (!int.TryParse(InvCustomAppId.Trim(), out var appId) || appId <= 0)
        {
            InventoryStatus = Loc.T("Inv_BadAppId");
            return;
        }
        var ctx = string.IsNullOrWhiteSpace(InvCustomContextId) ? "2" : InvCustomContextId.Trim();
        await LoadInventoryItemsAsync(new InventoryApp(appId, ctx, $"App {appId}", 0));
    }

    private async Task LoadInventoryItemsAsync(InventoryApp app)
    {
        if (_inventoryAcc is not { } acc) return;
        int seq = ++_invLoadSeq; // newest load wins; older ones bail out
        InventoryBusy = true;
        InventoryItems.Clear();
        InventoryStatus = Loc.T("Inv_Loading");
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token
                || !ulong.TryParse(acc.SteamId, out var steamId))
            {
                if (seq == _invLoadSeq) InventoryStatus = Loc.T("Inv_SignIn");
                return;
            }
            var items = await _inventory.FetchItemsAsync(steamId, app.AppId, app.ContextId, token);
            if (seq != _invLoadSeq || !ReferenceEquals(_inventoryAcc, acc) || !ShowInventory)
                return; // stale (a newer load started, or account switched / dialog closed)

            foreach (var it in items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                InventoryItems.Add(new TransferItem(it));
            InventoryStatus = items.Count == 0 ? Loc.T("Inv_NoItems") : "";
            _ = LoadItemImagesAsync(InventoryItems.ToList());
        }
        catch (Exception ex) { if (seq == _invLoadSeq) InventoryStatus = StatusLine.Error(ex); }
        finally { if (seq == _invLoadSeq) InventoryBusy = false; }
    }
}
