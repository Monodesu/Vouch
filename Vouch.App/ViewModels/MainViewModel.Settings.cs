using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Storage;

namespace Vouch.App.ViewModels;

/// <summary>
/// Settings — persisted to <c>settings.json</c> on every change — plus the two timers
/// they drive: periodic confirmation checks and clear-clipboard-after-copy.
/// (EncryptionEnabled is handled in MainViewModel.Encryption.cs; the manifest flag on
/// disk is its source of truth, not the settings file.)
/// </summary>
public partial class MainViewModel
{
    private bool _loadingSettings;
    private DispatcherTimer? _confirmTimer; // selected account
    private DispatcherTimer? _sweepTimer;   // all accounts (badge-only)
    private int _copyGeneration;

    public string[] ThemeOptions { get; } = { "Dark", "Light", "System" };
    [ObservableProperty] private string _theme = "Dark";

    // Language: the ComboBox shows the display labels; on disk we persist the culture code.
    private static readonly (string Label, string Code)[] Languages =
    {
        ("English", "en"),
        ("中文", "zh-CN"),
        ("Русский", "ru"),
        ("日本語", "ja"),
    };
    public string[] LanguageOptions { get; } = Array.ConvertAll(Languages, l => l.Label);
    [ObservableProperty] private string _languageOption = "English";

    private static string LabelForCode(string code) =>
        Array.Find(Languages, l => l.Code == code).Label ?? "English";
    private static string CodeForLabel(string label) =>
        Array.Find(Languages, l => l.Label == label) is { Code: { } c } ? c : "en";

    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _encryptionEnabled;
    [ObservableProperty] private bool _periodicChecking = true;
    [ObservableProperty] private int _periodicIntervalSeconds = 30;  // selected account, min 10
    [ObservableProperty] private int _sweepIntervalSeconds = 120;    // all accounts, min 60
    [ObservableProperty] private int _clipboardClearSeconds = 15;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _tradeUrl = "";
    [ObservableProperty] private bool _notifyOnNew = true; // system notification on new confirmation/offer
    [ObservableProperty] private bool _startOnBoot;        // launch on Windows sign-in (Startup shortcut)
    [ObservableProperty] private bool _warnIfUnencrypted = true; // top banner when maFiles aren't encrypted
    public bool AutostartSupported => Platform.Autostart.IsSupported;

    /// <summary>Drives the top "maFiles aren't encrypted" banner: shown when encryption is off, the user
    /// hasn't disabled the warning, and there are accounts at risk.</summary>
    public bool ShowUnencryptedWarning => !EncryptionEnabled && WarnIfUnencrypted && Accounts.Count > 0;

    /// <summary>Raise after any input to the banner state changes.</summary>
    internal void RefreshUnencryptedWarning() => OnPropertyChanged(nameof(ShowUnencryptedWarning));

    partial void OnWarnIfUnencryptedChanged(bool value) { SaveSettings(); RefreshUnencryptedWarning(); }

    [RelayCommand]
    private void OpenSettings() { CloseDialogs(); ShowSettings = true; }

    // First-run language chooser (shown before anything else when there's no settings file yet).
    [ObservableProperty] private bool _showLanguagePicker;

    [RelayCommand]
    private void PickLanguage(string? code)
    {
        var c = string.IsNullOrEmpty(code) ? "en" : code;
        Loc.I.SetLanguage(c);
        LanguageOption = LabelForCode(c);
        ShowLanguagePicker = false;
        SaveSettings(); // create the settings file so the chooser doesn't reappear
    }

    /// <summary>Opens an external URL (help links in settings) in the default browser.</summary>
    [RelayCommand]
    private async Task OpenLink(string? url)
    {
        if (url is { Length: > 0 } u && OpenUrl is not null) await OpenUrl(u);
    }

    private void LoadSettings()
    {
        var s = AppSettings.LoadFrom(AppPaths.SettingsPath);
        _loadingSettings = true;
        Theme = s.Theme;
        LanguageOption = LabelForCode(s.Language);
        Loc.I.SetLanguage(s.Language);
        MinimizeToTray = s.MinimizeToTray;
        StartMinimized = s.StartMinimized;
        PeriodicChecking = s.PeriodicChecking;
        PeriodicIntervalSeconds = Math.Max(10, s.PeriodicIntervalSeconds);
        SweepIntervalSeconds = Math.Max(60, s.SweepIntervalSeconds);
        ClipboardClearSeconds = s.ClipboardClearSeconds;
        ApiKey = s.ApiKey;
        TradeUrl = s.TradeUrl;
        NotifyOnNew = s.NotifyOnNew;
        WarnIfUnencrypted = s.WarnIfUnencrypted;
        StartOnBoot = Platform.Autostart.IsEnabled(); // reflect the real Startup-folder state
        _loadingSettings = false;
        RestartConfirmTimer();
        LoadLayoutFromIndex();       // account order + groups come from the repo index
        ApplyAccountOrder();         // reorder whatever's already loaded to the saved order
        ApplyGroupsToAccounts();     // then assign groups and build the grouped sidebar
    }

    private void SaveSettings()
    {
        if (_loadingSettings) return;
        new AppSettings
        {
            Theme = Theme,
            Language = CodeForLabel(LanguageOption),
            MinimizeToTray = MinimizeToTray,
            StartMinimized = StartMinimized,
            PeriodicChecking = PeriodicChecking,
            PeriodicIntervalSeconds = PeriodicIntervalSeconds,
            SweepIntervalSeconds = SweepIntervalSeconds,
            ClipboardClearSeconds = ClipboardClearSeconds,
            ApiKey = ApiKey,
            TradeUrl = TradeUrl,
            NotifyOnNew = NotifyOnNew,
            WarnIfUnencrypted = WarnIfUnencrypted,
            Encrypted = EncryptionEnabled, // keep the repo's at-rest flag intact on a full save
        }.SaveTo(AppPaths.SettingsPath);
    }

    partial void OnThemeChanged(string value) => SaveSettings();
    partial void OnLanguageOptionChanged(string value) { Loc.I.SetLanguage(CodeForLabel(value)); SaveSettings(); }
    partial void OnMinimizeToTrayChanged(bool value) => SaveSettings();
    partial void OnStartMinimizedChanged(bool value) => SaveSettings();
    partial void OnPeriodicCheckingChanged(bool value) { SaveSettings(); RestartConfirmTimer(); }
    partial void OnPeriodicIntervalSecondsChanged(int value) { SaveSettings(); RestartConfirmTimer(); }
    partial void OnSweepIntervalSecondsChanged(int value) { SaveSettings(); RestartConfirmTimer(); }
    partial void OnClipboardClearSecondsChanged(int value) => SaveSettings();
    partial void OnApiKeyChanged(string value) => SaveSettings();
    partial void OnTradeUrlChanged(string value) { SaveSettings(); OnPropertyChanged(nameof(HasTradeUrl)); }
    partial void OnNotifyOnNewChanged(bool value) => SaveSettings();
    partial void OnStartOnBootChanged(bool value) { if (!_loadingSettings) Platform.Autostart.Set(value); }

    // ---- periodic confirmation checking: two cadences ----
    //   selected account (fast, full list): every PeriodicIntervalSeconds (min 10)
    //   all accounts (badge counts only):   every SweepIntervalSeconds  (min 60)

    private void RestartConfirmTimer()
    {
        _confirmTimer?.Stop();
        _sweepTimer?.Stop();
        if (!PeriodicChecking) return;

        _confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(10, PeriodicIntervalSeconds)) };
        _confirmTimer.Tick += (_, _) => AutoCheckSelected();
        _confirmTimer.Start();

        _sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(60, SweepIntervalSeconds)) };
        _sweepTimer.Tick += (_, _) => _ = SweepAllConfirmationsAsync();
        _sweepTimer.Start();

        AutoCheckSelected();              // selected badge/list now
        _ = SweepAllConfirmationsAsync(); // other accounts' badges now
    }

    private void AutoCheckSelected()
    {
        // Stay quiet while locked, busy, inside a dialog, or mid batch sign-in.
        if (ShowUnlock || AnyDialogOpen || ConfirmationsBusy || BatchSignInActive) return;
        if (SelectedAccount is { IsReal: true, HasSession: true })
            _ = RefreshAllTabsAsync(forceRevalidate: false); // all four tabs; refresh-token check is throttled
    }

    // ---- clear-clipboard-after-copy ----

    /// <summary>Clears the clipboard N seconds after our copy, unless something newer was copied since.</summary>
    private async void ScheduleClipboardClear()
    {
        if (ClipboardClearSeconds <= 0 || ClipboardCopy is null) return;
        int gen = ++_copyGeneration;
        await Task.Delay(TimeSpan.FromSeconds(ClipboardClearSeconds));
        if (gen == _copyGeneration && ClipboardCopy is { } copy)
            await copy("");
    }
}
