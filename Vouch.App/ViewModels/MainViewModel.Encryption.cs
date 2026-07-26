using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;

namespace Vouch.App.ViewModels;

/// <summary>
/// Encrypted-at-rest flow: the startup unlock overlay, the set-passkey dialog, and the
/// settings toggle that turns directory encryption on/off (same on-disk format as the
/// original SDA, so encrypted maFiles interoperate).
/// </summary>
public partial class MainViewModel
{
    private bool _syncingEncryptionToggle;

    // ---- startup unlock (own overlay — NOT part of the regular dialog system,
    //      so it can't be dismissed without the passkey) ----
    [ObservableProperty] private bool _showUnlock;
    [ObservableProperty] private string _unlockPasskey = "";
    [ObservableProperty] private string _unlockStatus = "";

    [RelayCommand]
    private void Unlock()
    {
        if (!_repo.TryUnlock(UnlockPasskey))
        {
            UnlockStatus = StatusLine.Error(Loc.T("Enc_StatusWrongPasskey"));
            return;
        }

        UnlockPasskey = "";
        UnlockStatus = "";
        ShowUnlock = false;

        int i = 0;
        foreach (var acc in _repo.LoadAll())
            Accounts.Add(CreateAccountVm(acc, i++));
        ApplyAccountOrder();
        ApplyGroupsToAccounts();
        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
        RestartConfirmTimer(); // accounts are loaded now — start/refresh the checks + populate badges
    }

    // ---- set-passkey dialog (opened by turning the settings toggle on) ----
    [ObservableProperty] private string _setPasskey = "";
    [ObservableProperty] private string _setPasskeyConfirm = "";
    [ObservableProperty] private string _setPasskeyStatus = "";

    /// <summary>Keeps the settings toggle in sync with what's actually on disk.</summary>
    private void SyncEncryptionToggle()
    {
        _syncingEncryptionToggle = true;
        EncryptionEnabled = _repo.IsEncrypted;
        _syncingEncryptionToggle = false;
    }

    partial void OnEncryptionEnabledChanged(bool value)
    {
        if (_syncingEncryptionToggle || value == _repo.IsEncrypted) return;

        if (value)
        {
            // Collect a passkey first; the toggle reverts if the dialog is cancelled.
            SetPasskey = SetPasskeyConfirm = SetPasskeyStatus = "";
            CloseDialogs();
            ShowSetPasskey = true;
        }
        else
        {
            try
            {
                _repo.DisableEncryption();
            }
            catch (Exception)
            {
                SyncEncryptionToggle(); // e.g. still locked — put the toggle back
            }
        }
    }

    [RelayCommand]
    private void ConfirmSetPasskey()
    {
        if (string.IsNullOrEmpty(SetPasskey))
        {
            SetPasskeyStatus = Loc.T("Enc_StatusEnterPasskey");
            return;
        }
        if (SetPasskey != SetPasskeyConfirm)
        {
            SetPasskeyStatus = StatusLine.Error(Loc.T("Enc_StatusMismatch"));
            return;
        }

        try
        {
            _repo.EnableEncryption(SetPasskey);
            SetPasskey = SetPasskeyConfirm = "";
            SyncEncryptionToggle();
            OpenSettings(); // back to where the toggle lives
        }
        catch (Exception ex)
        {
            SetPasskeyStatus = StatusLine.Error(ex);
        }
    }

    [RelayCommand]
    private void CancelSetPasskey()
    {
        SetPasskey = SetPasskeyConfirm = "";
        SyncEncryptionToggle(); // reverts the toggle to off
        OpenSettings();
    }
}
