using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.Core.Steam;
using Vouch.Core.Storage;
using Vouch.App.Localization;

namespace Vouch.App.ViewModels;

/// <summary>maFile import (plaintext or encrypted) and export.</summary>
public partial class MainViewModel
{
    // ---------- import ----------
    [ObservableProperty] private string _importStatus = "";
    [ObservableProperty] private bool _importNeedsPassword;
    [ObservableProperty] private string _importPassword = "";
    private readonly List<string> _pendingImports = new(); // encrypted files awaiting a passkey

    [RelayCommand]
    private void OpenImport()
    {
        CloseDialogs();
        ImportStatus = "";
        ImportNeedsPassword = false;
        ImportPassword = "";
        _pendingImports.Clear();
        ShowImport = true;
    }

    [RelayCommand]
    private async Task DoImport()
    {
        if (PickMaFiles is null) return;
        var paths = await PickMaFiles();
        if (paths.Count > 0) ImportFiles(paths, null);
    }

    [RelayCommand]
    private void DecryptImport()
    {
        if (_pendingImports.Count > 0) ImportFiles(_pendingImports.ToArray(), ImportPassword);
    }

    /// <summary>Imports a maFile directly by path (used by tests / screenshot harness).</summary>
    public void ImportFromPath(string path) => ImportFiles(new[] { path }, null);

    /// <summary>
    /// Imports a batch of maFiles: plaintext files load immediately; encrypted ones need
    /// <paramref name="password"/> and are re-queued if it's missing or wrong — so one shared passkey
    /// can decrypt the whole selection in a single pass.
    /// </summary>
    private void ImportFiles(IReadOnlyList<string> paths, string? password)
    {
        int ok = 0, failed = 0;
        string? lastError = null;
        AccountViewModel? lastAccount = null;
        var pending = new List<string>();

        foreach (var path in paths)
        {
            var result = MaFileStore.LoadFile(path, password);
            switch (result.Status)
            {
                case MaFileLoadStatus.Ok:
                    lastAccount = AddImported(result.Account!);
                    ok++;
                    break;
                case MaFileLoadStatus.NeedsPassword:
                case MaFileLoadStatus.WrongPassword:
                    pending.Add(path); // (still) encrypted — needs the passkey
                    break;
                default:
                    failed++;
                    lastError = result.Error;
                    break;
            }
        }

        _pendingImports.Clear();
        _pendingImports.AddRange(pending);
        ImportNeedsPassword = pending.Count > 0;
        if (pending.Count == 0) ImportPassword = "";

        ImportStatus = DescribeImport(ok, failed, pending.Count, lastAccount, lastError, triedPassword: password is not null);
    }

    /// <summary>Adds (or replaces, on re-import) an imported account and persists it to disk.</summary>
    private AccountViewModel AddImported(SteamGuardAccount model)
    {
        var acc = CreateAccountVm(model, Accounts.Count % 5);
        if (Accounts.FirstOrDefault(a => a.SteamId == acc.SteamId) is { } dup) Accounts.Remove(dup);
        Accounts.Add(acc);
        SelectedAccount = acc;
        _repo.Save(model);
        SaveAccountOrder(); // remember the new account's position
        return acc;
    }

    private static string DescribeImport(int ok, int failed, int pending, AccountViewModel? last, string? error, bool triedPassword)
    {
        if (pending > 0)
            return triedPassword
                ? StatusLine.Error(Loc.T("Import_StatusWrongPasskeyN", pending))
                : Loc.T("Import_StatusNeedsPasskeyN", pending);

        if (ok == 1 && failed == 0 && last is not null)
            return StatusLine.Ok(Loc.T("Import_StatusOk", last.PersonaName, last.SteamId));
        if (ok > 0 && failed == 0)
            return StatusLine.Ok(Loc.T("Import_StatusBatchOk", ok));
        if (ok > 0)
            return StatusLine.Warn(Loc.T("Import_StatusBatchMixed", ok, failed));

        return failed == 1 && error is not null
            ? StatusLine.Error(Loc.T("Import_StatusFailed", error))
            : StatusLine.Error(Loc.T("Import_StatusFailedN", failed));
    }

    // ---------- export ----------
    [ObservableProperty] private bool _exportEncrypt = true;
    [ObservableProperty] private string _exportPassword = "";
    [ObservableProperty] private string _exportStatus = "";

    [RelayCommand]
    private void OpenExport() { CloseDialogs(); ExportStatus = ""; ExportPassword = ""; ShowExport = true; }

    [RelayCommand]
    private async Task DoExport()
    {
        if (PickExportPath is null || SelectedAccount is not { } acc) return;

        if (ExportEncrypt && string.IsNullOrEmpty(ExportPassword))
        {
            ExportStatus = Loc.T("Export_StatusEnterPasskey");
            return;
        }

        var suggested = $"{acc.SteamId}.maFile";
        var path = await PickExportPath(suggested);
        if (string.IsNullOrEmpty(path)) return;

        var model = new SteamGuardAccount
        {
            SharedSecret = Convert.ToBase64String(acc.SharedSecret),
            IdentitySecret = acc.IdentitySecret,
            RevocationCode = acc.RevocationCode,
            AccountName = acc.Username,
            Session = new SessionData { SteamId = ulong.TryParse(acc.SteamId, out var id) ? id : 0 }
        };

        try
        {
            if (ExportEncrypt)
            {
                MaFileStore.ExportEncrypted(model, path, ExportPassword);
                ExportStatus = StatusLine.Ok(Loc.T("Export_StatusEncrypted", path));
            }
            else
            {
                MaFileStore.ExportPlain(model, path);
                ExportStatus = StatusLine.Warn(Loc.T("Export_StatusPlain", path));
            }
        }
        catch (Exception ex)
        {
            ExportStatus = StatusLine.Error(Loc.T("Export_StatusFailed", ex.Message));
        }
    }
}
