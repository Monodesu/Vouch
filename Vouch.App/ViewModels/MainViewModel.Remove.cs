using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;

namespace Vouch.App.ViewModels;

/// <summary>Remove-account dialog: app-only delete, or revoke the authenticator on Steam.</summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _removeModeAppOnly = true;
    [ObservableProperty] private bool _removeModeToEmail;
    [ObservableProperty] private bool _removeModeFull;
    [ObservableProperty] private string _removeRevocationCode = "";
    [ObservableProperty] private bool _removeBusy;
    [ObservableProperty] private bool _removeDone;
    [ObservableProperty] private string _removeStatus = "";

    [RelayCommand]
    private void OpenRemove()
    {
        if (SelectedAccount is not { } acc) return;
        CloseDialogs();
        RemoveModeAppOnly = true;
        RemoveModeToEmail = RemoveModeFull = false;
        RemoveRevocationCode = acc.RevocationCode ?? "";
        RemoveBusy = RemoveDone = false;
        RemoveStatus = "";
        ShowRemove = true;
    }

    [RelayCommand]
    private async Task RemoveAccount()
    {
        if (RemoveBusy || RemoveDone || SelectedAccount is not { } acc) return;

        if (RemoveModeAppOnly)
        {
            DeleteFromApp(acc);
            ShowRemove = false;
            return;
        }

        // Revoking on Steam needs a live session + the revocation code.
        if (!acc.IsReal)
        {
            RemoveStatus = Loc.T("Remove_StatusDemo");
            return;
        }
        var code = RemoveRevocationCode.Trim();
        if (code.Length == 0)
        {
            RemoveStatus = Loc.T("Remove_StatusEnterCode");
            return;
        }

        RemoveBusy = true;
        RemoveStatus = Loc.T("Remove_StatusRemoving");
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
            {
                RemoveStatus = Loc.T("Remove_StatusSessionExpired");
                return;
            }
            var result = await _linker.RemoveAuthenticatorAsync(token, code, RemoveModeToEmail ? 1 : 2);
            if (result.Success)
            {
                DeleteFromApp(acc);
                RemoveDone = true;
                RemoveStatus = StatusLine.Ok(Loc.T(RemoveModeToEmail ? "Remove_StatusToEmail" : "Remove_StatusOff"));
            }
            else
            {
                RemoveStatus = StatusLine.Error(result.Error ?? Loc.T("Remove_StatusRefused"));
            }
        }
        catch (Exception ex) { RemoveStatus = StatusLine.Error(ex); }
        finally { RemoveBusy = false; }
    }

    // ---- batch deactivate (revoke the authenticator on Steam for several accounts) ----

    [ObservableProperty] private bool _deactivateToEmail;  // scheme: true = fall back to email codes, false = off
    [ObservableProperty] private bool _deactivateBusy;
    [ObservableProperty] private string _deactivateStatus = "";
    private List<AccountViewModel> _deactivateTargets = new();
    public string DeactivateCountText => Loc.T("Deactivate_Count", _deactivateTargets.Count);

    /// <summary>Opens the batch-deactivate dialog for the given accounts (sidebar right-click).</summary>
    public void BeginDeactivate(IReadOnlyList<AccountViewModel> accounts)
    {
        _deactivateTargets = accounts.Where(a => a.IsReal).ToList();
        if (_deactivateTargets.Count == 0) return;
        CloseDialogs();
        DeactivateToEmail = false;
        DeactivateBusy = false;
        DeactivateStatus = "";
        OnPropertyChanged(nameof(DeactivateCountText));
        ShowDeactivate = true;
    }

    /// <summary>Revokes each selected account's authenticator on Steam using its stored revocation code,
    /// then removes it from the app. Reports progress; a success toast at the end.</summary>
    [RelayCommand]
    private async Task DoDeactivate()
    {
        if (DeactivateBusy) return;
        DeactivateBusy = true;
        int ok = 0, failed = 0;
        var log = new StringBuilder();
        try
        {
            foreach (var acc in _deactivateTargets.ToList())
            {
                log.AppendLine($"▸ {acc.PersonaName}");
                var code = acc.RevocationCode?.Trim() ?? "";
                if (!acc.IsReal || code.Length == 0)
                {
                    failed++;
                    log.AppendLine("   " + Loc.T("Deactivate_NoCode"));
                    DeactivateStatus = log.ToString();
                    continue;
                }
                try
                {
                    if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
                    {
                        failed++;
                        log.AppendLine("   " + Loc.T("Remove_StatusSessionExpired"));
                        DeactivateStatus = log.ToString();
                        continue;
                    }
                    var result = await _linker.RemoveAuthenticatorAsync(token, code, DeactivateToEmail ? 1 : 2);
                    if (result.Success)
                    {
                        ok++;
                        DeleteFromApp(acc);
                        log.AppendLine("   " + Loc.T("Deactivate_Ok"));
                    }
                    else
                    {
                        failed++;
                        log.AppendLine("   " + (result.Error ?? Loc.T("Remove_StatusRefused")));
                    }
                }
                catch (Exception ex) { failed++; log.AppendLine("   " + ex.Message); }
                DeactivateStatus = log.ToString();
            }
            ShowToast(Loc.T("Deactivate_Done", ok, failed), failed == 0 ? ToastKind.Success : ToastKind.Error);
            if (failed == 0) ShowDeactivate = false;
        }
        finally { DeactivateBusy = false; }
    }

    /// <summary>Removes the account from the list and deletes its maFile + cached profile from disk.</summary>
    private void DeleteFromApp(AccountViewModel acc)
    {
        if (acc.IsReal && ulong.TryParse(acc.SteamId, out var steamId) && steamId != 0)
        {
            _repo.Delete(steamId);
            _profileCache.Delete(steamId);
        }
        Accounts.Remove(acc);
        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
        OnPropertyChanged(nameof(AccountsLabel));
        SaveAccountOrder();
    }
}
