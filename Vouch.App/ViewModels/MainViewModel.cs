using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.Core.Steam;
using Vouch.Core.Storage;
using Vouch.App.Localization;
using Vouch.App.Models;

namespace Vouch.App.ViewModels;

// The main VM is split by concern across partial-class files:
//   MainViewModel.cs               — accounts, selection, live code display, clipboard
//   MainViewModel.Confirmations.cs — trade confirmations + session renewal
//   MainViewModel.Profile.cs       — profile/avatar/ban refresh
//   MainViewModel.Dialogs.cs       — dialog plumbing + settings
//   MainViewModel.Wizard.cs        — new-authenticator linking (incl. phone flow)
//   MainViewModel.ImportExport.cs  — maFile import/export
//   MainViewModel.Remove.cs        — remove from app / revoke on Steam
//   MainViewModel.Login.cs         — Steam sign-in / session refresh
//   MainViewModel.Encryption.cs    — encrypted-at-rest: unlock overlay + passkey mgmt
//   MainViewModel.Settings.cs      — persisted settings + auto-check/clipboard timers
public partial class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _timer;
    private long _lastWindow = -1;

    /// <summary>Wired up by the View so copy commands hit the real system clipboard.</summary>
    public Func<string, Task>? ClipboardCopy { get; set; }

    /// <summary>Wired up by the View to launch URLs in the default browser.</summary>
    public Func<string, Task>? OpenUrl { get; set; }

    /// <summary>Wired up by the View: the main window's native handle, for system notifications.</summary>
    public Func<nint>? GetWindowHandle { get; set; }

    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    public ObservableCollection<ConfirmationItem> Confirmations { get; } = new();

    public bool IsEmpty => Confirmations.Count == 0;
    public string AccountsLabel => Accounts.Count == 1 ? "1 account" : $"{Accounts.Count} accounts";

    [ObservableProperty] private AccountViewModel? _selectedAccount;
    [ObservableProperty] private string _currentCode = "-----";
    [ObservableProperty] private double _ringSweep;        // 0..360 clockwise
    [ObservableProperty] private int _secondsRemaining;
    [ObservableProperty] private bool _passwordRevealed;
    [ObservableProperty] private bool _isEditingPassword;
    [ObservableProperty] private string _passwordEdit = "";

    /// <summary>The 30-second window that produced the currently displayed code (for verification/tests).</summary>
    public long DisplayWindow { get; private set; }
    [ObservableProperty] private bool _isUpdatingInfo;

    // Per-field "copied!" flashes.
    [ObservableProperty] private bool _codeCopied;
    [ObservableProperty] private bool _userCopied;
    [ObservableProperty] private bool _passCopied;

    public string Username => SelectedAccount?.Username ?? "";
    public string DisplayPassword
    {
        get
        {
            var pw = SelectedAccount?.Password ?? "";
            return PasswordRevealed ? pw : new string('•', pw.Length);
        }
    }

    private readonly MaFileRepository _repo;
    private readonly ProfileCache _profileCache = new(AppPaths.CacheDir);

    public MainViewModel() : this(new MaFileRepository(AppPaths.MaFilesDir)) { }

    public MainViewModel(MaFileRepository repo)
    {
        _repo = repo;

        if (repo.RequiresPasskey)
        {
            ShowUnlock = true; // accounts load after the passkey is entered
        }
        else
        {
            var persisted = repo.LoadAll();
            if (persisted.Count > 0)
            {
                int i = 0;
                foreach (var acc in persisted)
                    Accounts.Add(CreateAccountVm(acc, i++));
            }
            else if (Environment.GetEnvironmentVariable("VOUCH_DEMO") == "1")
            {
                LoadDemoAccounts(); // sample data for screenshots/dev only
            }
        }

        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
        SyncEncryptionToggle();
        LoadSettings();
        _ = SteamTime.EnsureAlignedAsync(); // codes follow Steam's clock once this lands
        Confirmations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        Accounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AccountsLabel));
        InitGroups(); // grouped sidebar: rebuild on account add/remove

        // First launch (no settings file yet): ask for a language before anything else.
        if (!System.IO.File.Exists(AppPaths.SettingsPath))
            ShowLanguagePicker = true;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private void LoadDemoAccounts()
    {
        //                     name          steamId              pend pal  username          password           persona          vac game
        Accounts.Add(new AccountViewModel("Aurora_Vex", "76561198000000001", 2, 0, "aurora_vex",     "Sunrise!7420",    "Aurora ✦ Vex", 0, 0));
        Accounts.Add(new AccountViewModel("nightowl",   "76561198000000002", 0, 1, "nightowl_tf",    "h00tXh00t_92",    "🦉 nightowl",  0, 0));
        Accounts.Add(new AccountViewModel("Praetor",    "76561198000000003", 5, 2, "praetor.legion", "Cohort#Rampart5", "Praetor [EU]", 1, 2));
        Accounts.Add(new AccountViewModel("kettle.tf",  "76561198000000004", 0, 3, "kettle_trades",  "st3ep!ng-t3a",    "kettle.tf ⇄",  0, 0));
        Accounts.Add(new AccountViewModel("m0chi",      "76561198000000005", 1, 4, "m0chi_daifuku",  "sw33t.rice.42",   "m0chi 🍡",     0, 1));
    }

    /// <summary>Builds an account VM from a maFile and restores its cached persona/avatar/bans.</summary>
    private AccountViewModel CreateAccountVm(SteamGuardAccount model, int paletteIndex)
    {
        var vm = AccountViewModel.FromMaFile(model, paletteIndex);
        if (ulong.TryParse(vm.SteamId, out var steamId))
        {
            var (profile, avatarBytes) = _profileCache.Load(steamId);
            if (profile is not null)
            {
                Avalonia.Media.Imaging.Bitmap? avatar = null;
                if (avatarBytes is not null)
                {
                    try { avatar = new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(avatarBytes)); }
                    catch (Exception) { } // stale/corrupt image — ignore
                }
                vm.ApplyCachedProfile(profile.PersonaName, avatar, profile.VacBans, profile.GameBans, profile.TradeBanned);
            }
        }
        return vm;
    }

    public bool HasSelection => SelectedAccount is not null;

    partial void OnSelectedAccountChanged(AccountViewModel? value)
    {
        _lastWindow = -1; // force code refresh
        PasswordRevealed = false;
        IsEditingPassword = false;
        ResetCopyFlags();
        if (value is null) CurrentCode = "-----";
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(DisplayPassword));
        RefreshSignInLabel();
        LoadConfirmations(value);
        ResetDetailTabs();
        Tick();
    }

    partial void OnPasswordRevealedChanged(bool value) => OnPropertyChanged(nameof(DisplayPassword));

    private void Tick()
    {
        var now = SteamTime.UtcNow;
        double remaining = SteamGuard.SecondsRemaining(now);
        SecondsRemaining = (int)Math.Ceiling(remaining);
        RingSweep = 360.0 * (remaining / SteamGuard.Period);

        long window = SteamGuard.CurrentWindow(now);
        DisplayWindow = window;
        if (window != _lastWindow && SelectedAccount is { } acc)
        {
            _lastWindow = window;
            CurrentCode = SteamGuard.GenerateCode(acc.SharedSecret, window);
            CodeCopied = false;
        }
    }

    private void ResetCopyFlags()
    {
        CodeCopied = UserCopied = PassCopied = false;
    }

    private async void Copy(string text)
    {
        if (ClipboardCopy is null) return;
        await ClipboardCopy(text);
        ScheduleClipboardClear();
    }

    [RelayCommand]
    private void CopyCode()
    {
        Copy(CurrentCode);
        ResetCopyFlags();
        CodeCopied = true;
    }

    [RelayCommand]
    private void CopyUsername()
    {
        Copy(Username);
        ResetCopyFlags();
        UserCopied = true;
    }

    [RelayCommand]
    private void CopyPassword()
    {
        Copy(SelectedAccount?.Password ?? "");
        ResetCopyFlags();
        PassCopied = true;
    }

    [RelayCommand]
    private void TogglePassword() => PasswordRevealed = !PasswordRevealed;

    [RelayCommand]
    private void BeginEditPassword()
    {
        if (SelectedAccount is null) return;
        PasswordEdit = SelectedAccount.Password;
        PasswordRevealed = true; // show what's being typed
        IsEditingPassword = true;
    }

    [RelayCommand]
    private void CancelEditPassword() => IsEditingPassword = false;

    /// <summary>Commits the edited password to the account and persists the maFile (one save, on confirm).</summary>
    [RelayCommand]
    private void SavePassword()
    {
        if (SelectedAccount is { } acc)
        {
            acc.Password = PasswordEdit;
            if (acc.Model is { } model)
            {
                try { _repo.Save(model); }
                catch (Exception ex) { ShowToast(ex.Message, ToastKind.Error); return; }
            }
            OnPropertyChanged(nameof(DisplayPassword));
            ShowToast(Loc.T("Detail_PasswordSaved"), ToastKind.Success);
        }
        IsEditingPassword = false;
    }

    [RelayCommand]
    private async Task OpenProfile()
    {
        if (SelectedAccount is { } acc && OpenUrl is not null)
            await OpenUrl(acc.ProfileUrl);
    }
}
