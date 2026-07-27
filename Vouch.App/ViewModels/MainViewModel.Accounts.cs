using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Steam;
using Vouch.Core.Storage;

namespace Vouch.App.ViewModels;

/// <summary>
/// Manual account ordering (drag-to-reorder, persisted as a SteamId list in settings) and the
/// multi-select batch actions — remove and export — that the sidebar's selection drives.
/// </summary>
public partial class MainViewModel
{
    private List<string> _accountOrder = new(); // SteamIds, in the user's chosen order
    private List<AccountViewModel> _batchRemoveTargets = new();

    [ObservableProperty] private int _batchRemoveCount;
    public string BatchRemoveSubText => Loc.T("Batch_RemoveSub", BatchRemoveCount);
    partial void OnBatchRemoveCountChanged(int value) => OnPropertyChanged(nameof(BatchRemoveSubText));

    // Multi-step confirmation: the button must be clicked through escalating warnings before it removes.
    [ObservableProperty] private int _batchRemoveStep;
    public string BatchRemoveButtonText => BatchRemoveStep switch
    {
        0 => Loc.T("Batch_RemoveButton"),
        1 => Loc.T("Batch_RemoveConfirm1"),
        _ => Loc.T("Batch_RemoveConfirm2", BatchRemoveCount),
    };
    partial void OnBatchRemoveStepChanged(int value) => OnPropertyChanged(nameof(BatchRemoveButtonText));

    /// <summary>Reorders the loaded accounts to match the saved order; unknown ones keep their spot at the end.</summary>
    private void ApplyAccountOrder()
    {
        if (_accountOrder.Count == 0 || Accounts.Count == 0) return;

        var rank = new Dictionary<string, int>();
        for (int i = 0; i < _accountOrder.Count; i++)
            rank.TryAdd(_accountOrder[i], i);

        int RankOf(AccountViewModel a) => rank.TryGetValue(a.SteamId, out var r) ? r : int.MaxValue;
        var ordered = Accounts.OrderBy(RankOf).ToList();
        ApplyOrder(ordered);
    }

    /// <summary>Rearranges the observable collection in place to match <paramref name="ordered"/>.</summary>
    private void ApplyOrder(IReadOnlyList<AccountViewModel> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            int cur = Accounts.IndexOf(ordered[i]);
            if (cur >= 0 && cur != i) Accounts.Move(cur, i);
        }
    }

    private void SaveAccountOrder()
    {
        _accountOrder = Accounts.Select(a => a.SteamId).ToList();
        SaveLayout(); // account order now persists to maFiles/entries.json
    }

    /// <summary>
    /// Drag-drop from the sidebar: moves the dragged accounts into <paramref name="group"/> and positions
    /// them before <paramref name="dropBefore"/> (an account in that group; null = end of the group),
    /// keeping their relative order. Setting the group is what makes a cross-group drag actually land in
    /// the target group instead of snapping back to the source group. Persists + rebuilds.
    /// </summary>
    public void MoveAccountsToGroupAt(IReadOnlyList<AccountViewModel> dragged, string group, AccountViewModel? dropBefore)
    {
        group = (group ?? "").Trim();
        var moving = Accounts.Where(dragged.Contains).ToList(); // current order, not drag order
        if (moving.Count == 0) return;

        foreach (var a in moving) a.Group = group;
        if (group.Length > 0 && !_groupOrder.Contains(group)) _groupOrder.Add(group);

        var remaining = Accounts.Where(a => !moving.Contains(a)).ToList();

        // Position in the flat list; RebuildSidebar re-groups by Group, so only the order relative to the
        // target group's other members matters.
        int insertAt;
        if (dropBefore is not null && !moving.Contains(dropBefore) && remaining.Contains(dropBefore))
        {
            insertAt = remaining.IndexOf(dropBefore);
        }
        else
        {
            // append after the last remaining account already in the target group (else end of list)
            int last = -1;
            for (int i = 0; i < remaining.Count; i++)
                if ((remaining[i].Group ?? "") == group) last = i;
            insertAt = last >= 0 ? last + 1 : remaining.Count;
        }

        var result = new List<AccountViewModel>(remaining);
        result.InsertRange(insertAt, moving);
        ApplyOrder(result);
        SaveLayout();
        RebuildSidebar(); // ensure a group-only change (no flat Move) still reflects
    }

    // ---- batch actions (driven by the sidebar's multi-selection) ----

    /// <summary>Opens the confirm dialog for removing several accounts from this app.</summary>
    public void BeginBatchRemove(IReadOnlyList<AccountViewModel> accounts)
    {
        _batchRemoveTargets = accounts.ToList();
        if (_batchRemoveTargets.Count == 0) return;
        CloseDialogs();
        BatchRemoveCount = _batchRemoveTargets.Count;
        BatchRemoveStep = 0;
        ShowBatchRemove = true;
    }

    [RelayCommand]
    private void ConfirmBatchRemove()
    {
        // Require three clicks (escalating warnings) before actually removing.
        if (BatchRemoveStep < 2) { BatchRemoveStep++; return; }
        RemoveAccountsFromApp(_batchRemoveTargets);
        _batchRemoveTargets = new();
        ShowBatchRemove = false;
    }

    /// <summary>Picks a folder and exports the given accounts there as plaintext maFiles.</summary>
    public async Task BatchExport(IReadOnlyList<AccountViewModel> accounts)
    {
        if (PickFolder is null || accounts.Count == 0) return;
        var folder = await PickFolder();
        if (string.IsNullOrEmpty(folder)) return;

        var (exported, failed) = ExportAccountsTo(accounts, folder, passkey: null);
        ShowToast(
            Loc.T(failed == 0 ? "Batch_ExportOk" : "Batch_ExportMixed", exported, failed),
            failed == 0 ? ToastKind.Success : ToastKind.Error);
    }

    /// <summary>Removes several accounts from this app (deletes their maFile + cached profile). App-only —
    /// it does not revoke the authenticator on Steam.</summary>
    public void RemoveAccountsFromApp(IReadOnlyList<AccountViewModel> accounts)
    {
        foreach (var acc in accounts.ToList())
        {
            if (acc.IsReal && ulong.TryParse(acc.SteamId, out var steamId) && steamId != 0)
            {
                _repo.Delete(steamId);
                _profileCache.Delete(steamId);
            }
            Accounts.Remove(acc);
        }
        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
        OnPropertyChanged(nameof(AccountsLabel));
        SaveAccountOrder();
    }

    /// <summary>Exports several accounts as maFiles into <paramref name="folder"/>. Encrypted (with the shared
    /// <paramref name="passkey"/>) when one is given, otherwise plaintext. Returns (exported, failed).</summary>
    public (int Exported, int Failed) ExportAccountsTo(IReadOnlyList<AccountViewModel> accounts, string folder, string? passkey)
    {
        int ok = 0, failed = 0;
        foreach (var acc in accounts)
        {
            try
            {
                var model = ToExportModel(acc);
                var path = System.IO.Path.Combine(folder, $"{acc.SteamId}.maFile");
                if (!string.IsNullOrEmpty(passkey))
                    MaFileStore.ExportEncrypted(model, path, passkey);
                else
                    MaFileStore.ExportPlain(model, path);
                ok++;
            }
            catch (Exception) { failed++; }
        }
        return (ok, failed);
    }

    /// <summary>Builds the maFile model to export for one account (mirrors the single-export in ImportExport).</summary>
    private static SteamGuardAccount ToExportModel(AccountViewModel acc) => new()
    {
        SharedSecret = Convert.ToBase64String(acc.SharedSecret),
        IdentitySecret = acc.IdentitySecret,
        RevocationCode = acc.RevocationCode,
        AccountName = acc.Username,
        AccountPassword = string.IsNullOrEmpty(acc.Password) ? null : acc.Password,
        AccountNotes = string.IsNullOrEmpty(acc.Notes) ? null : acc.Notes,
        Session = new SessionData { SteamId = ulong.TryParse(acc.SteamId, out var id) ? id : 0 },
    };
}
