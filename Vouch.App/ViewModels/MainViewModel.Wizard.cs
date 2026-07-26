using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Steam;

namespace Vouch.App.ViewModels;

/// <summary>
/// New-authenticator wizard (real linking).
/// Flow: log in -> (add + confirm a phone by email if the account has none) ->
///       AddAuthenticator (secrets + revocation code, texts SMS) ->
///       user enters SMS + saves revocation code -> FinalizeAddAuthenticator -> save maFile.
/// </summary>
public partial class MainViewModel
{
    // Shared with the remove dialog (MainViewModel.Remove.cs).
    private readonly SteamAuthenticatorLinker _linker = new();

    private SteamGuardAccount? _linkedAccount;
    private TaskCompletionSource<string>? _wizardEmailTcs;
    private ulong _wizardSteamId;
    private string _wizardAccessToken = "";

    [ObservableProperty] private int _wizardStep = 1; // 1 = login, 2 = phone, 3 = confirm
    [ObservableProperty] private string _wizardUsername = "";
    [ObservableProperty] private string _wizardPassword = "";
    [ObservableProperty] private string _wizardSmsCode = "";
    [ObservableProperty] private string _wizardRevocationCode = "";
    [ObservableProperty] private string _wizardRevocationConfirm = ""; // re-typed to confirm it was saved
    [ObservableProperty] private bool _showFinalizePrompt;             // separate finalize dialog
    [ObservableProperty] private string _wizardPhoneHint = "";
    [ObservableProperty] private bool _showEmailCodePrompt; // separate email-code dialog
    [ObservableProperty] private string _wizardEmailCode = "";
    [ObservableProperty] private bool _wizardBusy;
    [ObservableProperty] private string _wizardStatus = "";
    // phone sub-flow (accounts with no phone): 0 = enter number, 1 = await email, 2 = verify by SMS
    [ObservableProperty] private string _wizardPhoneNumber = "";
    [ObservableProperty] private string _wizardPhoneCountry = "US";
    [ObservableProperty] private int _wizardPhoneStage;
    [ObservableProperty] private string _wizardPhoneSmsCode = "";

    /// <summary>The wizard body hides while a separate sub-dialog (email code / finalize) is up.</summary>
    public bool WizardBodyVisible => ShowWizard && !ShowEmailCodePrompt && !ShowFinalizePrompt;
    partial void OnShowEmailCodePromptChanged(bool value) => OnPropertyChanged(nameof(WizardBodyVisible));
    partial void OnShowFinalizePromptChanged(bool value) => OnPropertyChanged(nameof(WizardBodyVisible));

    public bool WizardOnStep1 => WizardStep == 1;
    public bool WizardOnStep2 => WizardStep == 2;
    public bool WizardOnStep3 => WizardStep == 3;
    public bool WizardShowSendPhone => WizardOnStep2 && WizardPhoneStage == 0;
    public bool WizardShowContinuePhone => WizardOnStep2 && WizardPhoneStage == 1;
    public bool WizardShowVerifyPhone => WizardOnStep2 && WizardPhoneStage == 2;

    private void RaisePhoneStageVisibility()
    {
        OnPropertyChanged(nameof(WizardShowSendPhone));
        OnPropertyChanged(nameof(WizardShowContinuePhone));
        OnPropertyChanged(nameof(WizardShowVerifyPhone));
    }

    partial void OnWizardStepChanged(int value)
    {
        OnPropertyChanged(nameof(WizardOnStep1));
        OnPropertyChanged(nameof(WizardOnStep2));
        OnPropertyChanged(nameof(WizardOnStep3));
        RaisePhoneStageVisibility();
    }

    partial void OnWizardPhoneStageChanged(int value) => RaisePhoneStageVisibility();

    [RelayCommand]
    private void OpenWizard()
    {
        CloseDialogs();
        WizardStep = 1;
        WizardUsername = WizardPassword = WizardSmsCode = WizardRevocationCode = WizardPhoneHint = "";
        ShowEmailCodePrompt = false;
        ShowFinalizePrompt = false;
        WizardRevocationConfirm = "";
        WizardEmailCode = "";
        WizardPhoneNumber = "";
        WizardPhoneCountry = "US";
        WizardPhoneStage = 0;
        WizardPhoneSmsCode = "";
        WizardBusy = false;
        WizardStatus = "";
        _linkedAccount = null;
        _wizardSteamId = 0;
        _wizardAccessToken = "";
        ShowWizard = true;
    }

    /// <summary>Step 1: log in, then either add the authenticator (has phone) or start the phone flow.</summary>
    [RelayCommand]
    private async Task WizardLoginAndAdd()
    {
        if (WizardBusy || string.IsNullOrWhiteSpace(WizardUsername) || string.IsNullOrEmpty(WizardPassword))
        {
            WizardStatus = Loc.T("Login_StatusEnterCreds");
            return;
        }

        WizardBusy = true;
        ShowEmailCodePrompt = false;
        WizardStatus = ""; // the "Working…" button covers progress; no logging-in line
        try
        {
            // No shared secret yet — the account has no authenticator, so Steam uses an email code.
            var authenticator = new MaFileAuthenticator(null, ProvideWizardEmailCodeAsync);
            var login = await _login.LoginAsync(WizardUsername.Trim(), WizardPassword, authenticator);
            _wizardSteamId = login.SteamId;
            _wizardAccessToken = login.AccessToken;

            WizardStatus = Loc.T("Wizard_StatusChecking");
            // Add straight away: if the account already has a phone, Steam links it immediately and we
            // skip the phone step entirely. Only a NeedsPhone result starts the phone sub-flow — this
            // mirrors the original SDA and avoids a stale HasPhone pre-check pulling verified accounts
            // through the phone screen.
            await TryAddAuthenticatorAsync();
        }
        catch (Exception ex)
        {
            WizardStatus = StatusLine.Error(ex);
        }
        finally
        {
            WizardBusy = false;
        }
    }

    /// <summary>Step 2a: submit a phone number — Steam emails a confirmation link.</summary>
    [RelayCommand]
    private async Task WizardSendPhoneEmail()
    {
        if (WizardBusy || string.IsNullOrWhiteSpace(WizardPhoneNumber))
        {
            WizardStatus = Loc.T("Wizard_StatusEnterPhone");
            return;
        }
        WizardBusy = true;
        WizardStatus = Loc.T("Wizard_StatusAddingPhone");
        try
        {
            var country = WizardPhoneCountry.Trim();
            if (country.Length == 0)
                country = await _linker.GetUserCountryAsync(_wizardSteamId, _wizardAccessToken) ?? "";
            var result = await _linker.SetPhoneAsync(_wizardAccessToken, WizardPhoneNumber.Trim(), country);
            if (result.Ok)
            {
                WizardPhoneStage = 1; // await email confirmation
                WizardStatus = Loc.T("Wizard_StatusPhoneEmailSent", result.Email ?? "");
            }
            else
            {
                WizardStatus = StatusLine.Error(result.Error ?? Loc.T("Wizard_StatusPhoneRejected"));
            }
        }
        catch (Exception ex) { WizardStatus = StatusLine.Error(ex); }
        finally { WizardBusy = false; }
    }

    /// <summary>Step 2b: after the email link is clicked, text an SMS to verify the phone.</summary>
    [RelayCommand]
    private async Task WizardContinueAfterEmail()
    {
        if (WizardBusy) return;
        WizardBusy = true;
        WizardStatus = Loc.T("Wizard_StatusCheckingEmail");
        try
        {
            if (await _linker.IsAwaitingEmailConfirmationAsync(_wizardAccessToken))
            {
                WizardStatus = Loc.T("Wizard_StatusStillWaiting");
                return;
            }
            await _linker.SendPhoneVerificationSmsAsync(_wizardAccessToken);
            WizardPhoneStage = 2; // enter the phone-verification SMS
            WizardStatus = Loc.T("Wizard_StatusPhoneTexted");
        }
        catch (Exception ex) { WizardStatus = StatusLine.Error(ex); }
        finally { WizardBusy = false; }
    }

    /// <summary>Step 2c: verify the phone with its SMS code, then add the authenticator.</summary>
    [RelayCommand]
    private async Task WizardVerifyPhone()
    {
        if (WizardBusy) return;
        if (string.IsNullOrWhiteSpace(WizardPhoneSmsCode))
        {
            WizardStatus = Loc.T("Wizard_StatusEnterPhoneSms");
            return;
        }
        WizardBusy = true;
        WizardStatus = Loc.T("Wizard_StatusVerifyingPhone");
        try
        {
            await _linker.VerifyPhoneWithCodeAsync(_wizardAccessToken, WizardPhoneSmsCode.Trim());
            await TryAddAuthenticatorAsync();
        }
        catch (Exception ex) { WizardStatus = StatusLine.Error(ex); }
        finally { WizardBusy = false; }
    }

    /// <summary>Shared: call AddAuthenticator and advance to the SMS/finalize step on success.</summary>
    private async Task TryAddAuthenticatorAsync()
    {
        WizardStatus = Loc.T("Wizard_StatusAddingAuth");
        var deviceId = SteamAuthenticatorLinker.GenerateDeviceId();
        var add = await _linker.AddAuthenticatorAsync(_wizardSteamId, _wizardAccessToken, deviceId);

        switch (add.Status)
        {
            case AddAuthenticatorStatus.Success:
                _linkedAccount = add.Account;
                WizardRevocationCode = add.Account!.RevocationCode ?? "";
                WizardPhoneHint = add.PhoneHint ?? "";
                WizardStep = 3;
                WizardStatus = "";
                break;
            case AddAuthenticatorStatus.NeedsPhone:
                WizardStep = 2; // no phone on the account — collect + confirm one first
                WizardStatus = Loc.T("Wizard_StatusNoPhone");
                break;
            case AddAuthenticatorStatus.AuthenticatorPresent:
                WizardStatus = Loc.T("Wizard_StatusAuthPresent");
                break;
            default:
                WizardStatus = StatusLine.Error(add.Error ?? Loc.T("Wizard_StatusAddFailed"));
                break;
        }
    }

    private Task<string> ProvideWizardEmailCodeAsync(string email, bool previousWasIncorrect)
    {
        _wizardEmailTcs = new TaskCompletionSource<string>();
        Dispatcher.UIThread.Post(() =>
        {
            WizardEmailCode = "";
            ShowEmailCodePrompt = true; // pop the separate email-code dialog
            // "Steam emailed a code to X" goes to a toast rather than the wizard body.
            ShowToast(
                previousWasIncorrect ? Loc.T("Login_StatusEmailWrong", email) : Loc.T("Wizard_StatusEmailSent", email),
                previousWasIncorrect ? ToastKind.Error : ToastKind.Info);
        });
        return _wizardEmailTcs.Task;
    }

    [RelayCommand]
    private void WizardSubmitEmail()
    {
        _wizardEmailTcs?.TrySetResult(WizardEmailCode.Trim());
        ShowEmailCodePrompt = false;
    }

    /// <summary>Dismisses the email-code dialog and aborts the pending login (empty code).</summary>
    [RelayCommand]
    private void CancelEmailCode()
    {
        ShowEmailCodePrompt = false;
        _wizardEmailTcs?.TrySetResult("");
    }

    /// <summary>Step 3: reveal the revocation code, then open the finalize dialog.</summary>
    [RelayCommand]
    private void OpenFinalizePrompt()
    {
        WizardSmsCode = "";
        WizardRevocationConfirm = "";
        WizardStatus = "";
        ShowFinalizePrompt = true;
    }

    /// <summary>Dismisses the finalize dialog, back to the revocation-code screen.</summary>
    [RelayCommand]
    private void CancelFinalize() => ShowFinalizePrompt = false;

    /// <summary>Finalize dialog: check the re-typed revocation code, finalize with the verification
    /// code, and persist the new maFile (with username + password). A wrong revocation code aborts.</summary>
    [RelayCommand]
    private async Task WizardFinish()
    {
        if (WizardBusy) return;
        if (_linkedAccount is not { } linked || linked.Session is not { } session)
        {
            WizardStatus = Loc.T("Wizard_StatusNoPending");
            return;
        }
        if (string.IsNullOrWhiteSpace(WizardSmsCode))
        {
            WizardStatus = Loc.T("Wizard_StatusEnterSms");
            return;
        }
        // The re-typed revocation code must match the one shown, or we abort the whole setup.
        if (!WizardRevocationConfirm.Trim().Equals(WizardRevocationCode.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            ShowFinalizePrompt = false;
            ShowWizard = false;
            ShowToast(Loc.T("Wizard_RevocationMismatch"), ToastKind.Error);
            return;
        }

        WizardBusy = true;
        WizardStatus = Loc.T("Wizard_StatusFinalizing");
        try
        {
            var result = await _linker.FinalizeAsync(session.SteamId, session.AccessToken!, linked, WizardSmsCode.Trim());
            switch (result)
            {
                case FinalizeStatus.Success:
                    linked.AccountName = string.IsNullOrEmpty(linked.AccountName) ? WizardUsername.Trim() : linked.AccountName;
                    linked.AccountPassword = WizardPassword; // store the password alongside username + revocation code
                    _repo.Save(linked); // persist the new maFile
                    var acc = CreateAccountVm(linked, Accounts.Count % 5);
                    Accounts.Add(acc);
                    SelectedAccount = acc;
                    ShowFinalizePrompt = false;
                    ShowWizard = false;
                    ShowToast(Loc.T("Wizard_Added", linked.AccountName ?? WizardUsername.Trim()), ToastKind.Success);
                    break;
                case FinalizeStatus.BadSmsCode:
                    WizardStatus = StatusLine.Error(Loc.T("Wizard_StatusBadSms"));
                    break;
                case FinalizeStatus.UnableToSyncTime:
                    WizardStatus = StatusLine.Error(Loc.T("Wizard_StatusTimeSync"));
                    break;
                default:
                    WizardStatus = StatusLine.Error(Loc.T("Wizard_StatusFinalizeFailed"));
                    break;
            }
        }
        catch (Exception ex)
        {
            WizardStatus = StatusLine.Error(ex);
        }
        finally
        {
            WizardBusy = false;
        }
    }
}
