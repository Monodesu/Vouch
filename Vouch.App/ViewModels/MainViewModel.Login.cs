using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Steam;

namespace Vouch.App.ViewModels;

/// <summary>Steam sign-in dialog: refreshes an account's session for trade confirmations.</summary>
public partial class MainViewModel
{
    // Shared with the wizard (MainViewModel.Wizard.cs).
    private readonly SteamLoginService _login = new();

    private TaskCompletionSource<string>? _emailCodeTcs;

    /// <summary>True when the selected account is signed in and its access token hasn't expired yet.</summary>
    public bool HasValidSession =>
        SelectedAccount is { IsReal: true, AccessToken: { } token }
        && !SteamSessionService.IsExpired(token, TimeSpan.Zero);

    /// <summary>The header button label: "Re-Sign in" while a valid session exists, else "Sign in".</summary>
    public string SignInLabel => Loc.T(HasValidSession ? "Detail_ReSignIn" : "Detail_SignIn");

    /// <summary>Re-evaluates the sign-in button label (after selection changes or a login).</summary>
    public void RefreshSignInLabel()
    {
        OnPropertyChanged(nameof(HasValidSession));
        OnPropertyChanged(nameof(SignInLabel));
    }

    [ObservableProperty] private string _loginUsername = "";
    [ObservableProperty] private string _loginPassword = "";
    [ObservableProperty] private bool _loginBusy;
    [ObservableProperty] private string _loginStatus = "";
    [ObservableProperty] private bool _loginNeedsEmail;
    [ObservableProperty] private string _loginEmailCode = "";

    // ---- batch sign-in (driven by the sidebar selection) ----
    [ObservableProperty] private bool _batchSignInActive;
    [ObservableProperty] private string _batchSignInProgress = "";
    private List<AccountViewModel> _signInQueue = new();
    private int _signInIndex;
    private int _signInSuccess;

    /// <summary>
    /// Signs in several accounts through the login dialog, one at a time: an account with a stored
    /// password is attempted automatically and advances on success; the dialog stops (keeping the
    /// account loaded) on any that need a password entered — missing, wrong, or an email code.
    /// </summary>
    public void BatchSignIn(IReadOnlyList<AccountViewModel> accounts)
    {
        _signInQueue = accounts.Where(a => a.IsReal).ToList();
        _signInIndex = 0;
        _signInSuccess = 0;
        if (_signInQueue.Count > 0) NextBatchSignIn();
    }

    private void NextBatchSignIn()
    {
        if (_signInIndex >= _signInQueue.Count) { FinishBatchSignIn(); return; }

        var acc = _signInQueue[_signInIndex];
        SelectedAccount = acc; // DoLogin saves the session into SelectedAccount's model
        LoginUsername = acc.Username;
        LoginPassword = acc.Password;
        LoginEmailCode = "";
        LoginNeedsEmail = false;
        LoginBusy = false;
        LoginStatus = "";
        BatchSignInActive = true;
        BatchSignInProgress = Loc.T("Batch_SignInProgress", _signInIndex + 1, _signInQueue.Count);
        ShowLogin = true;

        // Auto-attempt when we already have a password; otherwise wait for the user to type it.
        if (!string.IsNullOrEmpty(acc.Password)) DoLoginCommand.Execute(null);
    }

    private void AdvanceBatchSignIn()
    {
        _signInIndex++;
        NextBatchSignIn();
    }

    [RelayCommand]
    private void SkipBatchSignIn() => AdvanceBatchSignIn();

    private void FinishBatchSignIn()
    {
        int success = _signInSuccess, total = _signInQueue.Count;
        _signInQueue = new();
        _signInIndex = _signInSuccess = 0;
        BatchSignInActive = false;
        ShowLogin = false;
        RefreshSignInLabel();
        ShowToast(Loc.T("Batch_SignInDone", success, total), ToastKind.Success);
    }

    /// <summary>The login dialog's close button: stops a running batch (reporting progress), else closes.</summary>
    [RelayCommand]
    private void LoginClose()
    {
        if (BatchSignInActive) FinishBatchSignIn();
        else CloseDialogs();
    }

    [RelayCommand]
    private void OpenLogin()
    {
        CloseDialogs();
        BatchSignInActive = false;
        LoginUsername = SelectedAccount?.Username ?? "";
        LoginPassword = SelectedAccount?.Password ?? ""; // reuse the stored password if the account has one
        LoginStatus = "";
        LoginBusy = false;
        LoginNeedsEmail = false;
        LoginEmailCode = "";
        ShowLogin = true;
    }

    [RelayCommand]
    private async Task DoLogin()
    {
        if (LoginBusy || string.IsNullOrWhiteSpace(LoginUsername) || string.IsNullOrEmpty(LoginPassword))
        {
            LoginStatus = Loc.T("Login_StatusEnterCreds");
            return;
        }

        LoginBusy = true;
        LoginNeedsEmail = false;
        LoginStatus = Loc.T("Login_StatusConnecting");

        var authenticator = new MaFileAuthenticator(SelectedAccount?.SharedSecret, ProvideEmailCodeAsync);

        try
        {
            var password = LoginPassword;
            var result = await _login.LoginAsync(LoginUsername.Trim(), password, authenticator);

            if (SelectedAccount is { } acc)
            {
                ApplyLoginResult(acc, result, password); // saves the working password too
                OnPropertyChanged(nameof(DisplayPassword));
            }
            LoginPassword = "";
            LoginStatus = StatusLine.Ok(Loc.T("Login_StatusSuccess"));
            RefreshSignInLabel(); // now signed in -> "Re-Sign in"

            if (BatchSignInActive)
            {
                _signInSuccess++;
                AdvanceBatchSignIn(); // move to the next queued account
            }
            else
            {
                ShowToast(Loc.T("Login_ToastSuccess"), ToastKind.Success);
                ShowLogin = false; // the toast confirms it; close the dialog on success
            }
        }
        catch (Exception ex)
        {
            LoginStatus = StatusLine.Error(ex);
            if (!BatchSignInActive) ShowToast(ex.Message, ToastKind.Error); // batch keeps the dialog for a retry
        }
        finally
        {
            LoginBusy = false;
            LoginNeedsEmail = false;
        }
    }

    /// <summary>Writes a fresh session — and the password that worked — into the account's maFile.</summary>
    private void ApplyLoginResult(AccountViewModel acc, SteamLoginResult result, string password)
    {
        if (acc.Model is not { } model) return;
        if (!string.IsNullOrEmpty(password)) acc.Password = password; // remember it (writes account_password)
        model.Session ??= new SessionData();
        model.Session.SteamId = result.SteamId;
        model.Session.AccessToken = result.AccessToken;
        model.Session.RefreshToken = result.RefreshToken;
        model.FullyEnrolled = true;
        _repo.Save(model);
        acc.HasSession = true;
        acc.SessionInvalid = false; // fresh session — clears any "dead" mark (also refreshes the stripe)
        acc.RefreshSessionState(); // update the sidebar stripe / avatar ring colour now
    }

    /// <summary>Called by the authenticator (off the UI thread) when Steam wants an email code.</summary>
    private Task<string> ProvideEmailCodeAsync(string email, bool previousWasIncorrect)
    {
        _emailCodeTcs = new TaskCompletionSource<string>();
        Dispatcher.UIThread.Post(() =>
        {
            LoginNeedsEmail = true;
            LoginEmailCode = "";
            LoginStatus = previousWasIncorrect
                ? Loc.T("Login_StatusEmailWrong", email)
                : Loc.T("Login_StatusEmailSent", email);
        });
        return _emailCodeTcs.Task;
    }

    [RelayCommand]
    private void SubmitEmailCode()
    {
        _emailCodeTcs?.TrySetResult(LoginEmailCode.Trim());
        LoginNeedsEmail = false;
        LoginStatus = Loc.T("Login_StatusSubmitting");
    }
}
