using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.App.Platform;
using Vouch.Core.Steam;
using Vouch.Core.Storage;

namespace Vouch.App.ViewModels;

/// <summary>One target account in the CS2-sync dialog (checkbox + "already has CS2 here?" hint).</summary>
public sealed partial class Cs2TargetItem : ObservableObject
{
    public AccountViewModel Account { get; }
    public bool HasCs2 { get; }
    public Cs2TargetItem(AccountViewModel account, bool hasCs2) { Account = account; HasCs2 = hasCs2; }
    public string Persona => Account.PersonaName;
    public string SteamId => Account.SteamId;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One backup set in the restore dropdown.</summary>
public sealed class Cs2BackupOption
{
    public Cs2ConfigSync.Cs2BackupSet Set { get; }
    public Cs2BackupOption(Cs2ConfigSync.Cs2BackupSet set) { Set = set; }
    public string Display
    {
        get
        {
            var s = Set.Stamp; // yyyyMMdd-HHmmss
            var pretty = s.Length == 15
                ? $"{s[..4]}-{s.Substring(4, 2)}-{s.Substring(6, 2)} {s.Substring(9, 2)}:{s.Substring(11, 2)}:{s.Substring(13, 2)}"
                : s;
            return $"{pretty}  ({Set.AccountIds.Count})";
        }
    }
}

/// <summary>One account inside a backup that can be restored.</summary>
public sealed partial class Cs2RestoreItem : ObservableObject
{
    public ulong SteamId { get; }
    public string Display { get; }
    public Cs2RestoreItem(ulong steamId, string display) { SteamId = steamId; Display = display; }
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>
/// CS2 config sync: copy one account's crosshair / binds / settings onto other accounts (alts on the same
/// PC). Right-click an account to make it the source, then pick targets (all / a group / hand-picked).
/// Local file copy only — see <see cref="Cs2ConfigSync"/>.
/// </summary>
public partial class MainViewModel
{
    private Cs2ConfigSync? _cs2;
    private AccountViewModel? _cs2Source;

    [ObservableProperty] private bool _showCs2Sync;
    public ObservableCollection<Cs2TargetItem> Cs2Targets { get; } = new();

    [ObservableProperty] private bool _cs2Settings = true;  // convars (crosshair / sens / viewmodel / HUD…)
    [ObservableProperty] private bool _cs2Keys = true;      // key binds
    [ObservableProperty] private bool _cs2Video;            // per-PC graphics; off by default
    [ObservableProperty] private bool _cs2Backup = true;
    [ObservableProperty] private string _cs2Status = "";

    public bool Cs2SettingsAvailable { get; private set; }
    public bool Cs2KeysAvailable { get; private set; }
    public bool Cs2VideoAvailable { get; private set; }
    public string Cs2SourceName => _cs2Source?.PersonaName ?? "";
    public string[] Cs2GroupOptions => GroupNames.ToArray();
    public bool Cs2HasGroups => GroupNames.Count > 0;

    /// <summary>Opens the sync dialog with <paramref name="source"/> as the config to copy from.</summary>
    public void BeginCs2Sync(AccountViewModel? source)
    {
        if (source is not { IsReal: true }) return;
        var userdata = SteamPaths.UserdataDir();
        if (userdata is null)
        {
            ShowToast(Loc.T("Cs2_NoSteam"), ToastKind.Error);
            return;
        }
        _cs2 = new Cs2ConfigSync(userdata);
        _cs2Source = source;
        Cs2RestoreMode = false;

        if (!ulong.TryParse(source.SteamId, out var srcId) || !_cs2.HasCs2(srcId))
        {
            ShowToast(Loc.T("Cs2_SourceHasNone"), ToastKind.Error);
            return;
        }

        var avail = _cs2.AvailableParts(srcId);
        Cs2SettingsAvailable = avail.HasFlag(Cs2Parts.Settings);
        Cs2KeysAvailable = avail.HasFlag(Cs2Parts.Keys);
        Cs2VideoAvailable = avail.HasFlag(Cs2Parts.Video);
        Cs2Settings = Cs2SettingsAvailable;
        Cs2Keys = Cs2KeysAvailable;
        Cs2Video = false;
        Cs2Status = "";

        Cs2Targets.Clear();
        foreach (var a in Accounts.Where(a => a.IsReal && !ReferenceEquals(a, source)))
            Cs2Targets.Add(new Cs2TargetItem(a, ulong.TryParse(a.SteamId, out var id) && _cs2.HasCs2(id)));

        OnPropertyChanged(nameof(Cs2SourceName));
        OnPropertyChanged(nameof(Cs2SettingsAvailable));
        OnPropertyChanged(nameof(Cs2KeysAvailable));
        OnPropertyChanged(nameof(Cs2VideoAvailable));
        OnPropertyChanged(nameof(Cs2GroupOptions));
        OnPropertyChanged(nameof(Cs2HasGroups));
        CloseDialogs();
        ShowCs2Sync = true;
    }

    [RelayCommand]
    private void Cs2SelectAll() { foreach (var t in Cs2Targets) t.IsSelected = true; }

    [RelayCommand]
    private void Cs2SelectNone() { foreach (var t in Cs2Targets) t.IsSelected = false; }

    /// <summary>Ticks the accounts in <paramref name="group"/> ("" = all).</summary>
    [RelayCommand]
    private void Cs2SelectGroup(string? group)
    {
        var g = group ?? "";
        foreach (var t in Cs2Targets)
            t.IsSelected = g.Length == 0 || string.Equals(t.Account.Group ?? "", g, StringComparison.Ordinal);
    }

    private Cs2Parts SelectedParts()
    {
        var p = Cs2Parts.None;
        if (Cs2Settings) p |= Cs2Parts.Settings;
        if (Cs2Keys) p |= Cs2Parts.Keys;
        if (Cs2Video) p |= Cs2Parts.Video;
        return p;
    }

    // ---- restore from backup ----

    private const ulong SteamId64Base = 76561197960265728UL;
    private string Cs2BackupsRoot => Path.Combine(AppPaths.DataDir, "cs2-backups");

    [ObservableProperty] private bool _cs2RestoreMode;
    public ObservableCollection<Cs2BackupOption> Cs2BackupList { get; } = new();
    [ObservableProperty] private Cs2BackupOption? _cs2SelectedBackup;
    public ObservableCollection<Cs2RestoreItem> Cs2RestoreTargets { get; } = new();
    public bool Cs2HasBackups => Cs2BackupList.Count > 0;

    /// <summary>Opens the dialog in restore mode: pick a backup set, then which accounts to roll back.</summary>
    public void BeginCs2Restore()
    {
        var userdata = SteamPaths.UserdataDir();
        if (userdata is null) { ShowToast(Loc.T("Cs2_NoSteam"), ToastKind.Error); return; }
        _cs2 = new Cs2ConfigSync(userdata);
        Cs2RestoreMode = true;
        Cs2Status = "";

        Cs2BackupList.Clear();
        foreach (var b in Cs2ConfigSync.ListBackups(Cs2BackupsRoot)) Cs2BackupList.Add(new Cs2BackupOption(b));
        OnPropertyChanged(nameof(Cs2HasBackups));
        Cs2SelectedBackup = Cs2BackupList.FirstOrDefault();

        CloseDialogs();
        ShowCs2Sync = true;
    }

    partial void OnCs2SelectedBackupChanged(Cs2BackupOption? value)
    {
        Cs2RestoreTargets.Clear();
        if (value is null) return;
        foreach (var accId in value.Set.AccountIds)
        {
            ulong sid = accId + SteamId64Base;
            var known = Accounts.FirstOrDefault(a => ulong.TryParse(a.SteamId, out var s) && s == sid);
            Cs2RestoreTargets.Add(new Cs2RestoreItem(sid, known?.PersonaName ?? accId.ToString()));
        }
    }

    [RelayCommand]
    private async Task RunCs2Restore()
    {
        if (_cs2 is null || Cs2SelectedBackup is not { } opt) return;
        var targets = Cs2RestoreTargets.Where(t => t.IsSelected).Select(t => t.SteamId).ToList();
        if (targets.Count == 0) { Cs2Status = StatusLine.Error(Loc.T("Cs2_PickTarget")); return; }

        Cs2Status = Loc.T("Cs2_Working");
        var dir = opt.Set.Dir;
        var results = await Task.Run(() => targets.Select(t => _cs2!.Restore(dir, t)).ToList());
        int ok = results.Count(r => r.Ok), failed = results.Count - ok;

        ShowCs2Sync = false;
        ShowToast(Loc.T("Cs2_Restored", ok, failed), failed == 0 ? ToastKind.Success : ToastKind.Error);
    }

    [RelayCommand]
    private async Task RunCs2Sync()
    {
        if (_cs2 is null || _cs2Source is null || !ulong.TryParse(_cs2Source.SteamId, out var srcId)) return;
        var parts = SelectedParts();
        if (parts == Cs2Parts.None) { Cs2Status = StatusLine.Error(Loc.T("Cs2_PickPart")); return; }

        var targets = Cs2Targets.Where(t => t.IsSelected)
            .Select(t => ulong.TryParse(t.Account.SteamId, out var id) ? id : 0UL)
            .Where(id => id != 0).ToList();
        if (targets.Count == 0) { Cs2Status = StatusLine.Error(Loc.T("Cs2_PickTarget")); return; }

        string? backupRoot = null;
        if (Cs2Backup)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            backupRoot = Path.Combine(AppPaths.DataDir, "cs2-backups", stamp);
        }

        Cs2Status = Loc.T("Cs2_Working");
        var results = await Task.Run(() => _cs2.Sync(srcId, targets, parts, backupRoot));
        int ok = results.Count(r => r.Ok), failed = results.Count - ok;

        ShowCs2Sync = false;
        ShowToast(Loc.T("Cs2_Done", ok, failed), failed == 0 ? ToastKind.Success : ToastKind.Error);
    }
}
