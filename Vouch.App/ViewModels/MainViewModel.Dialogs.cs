using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vouch.App.ViewModels;

/// <summary>Dialog-overlay plumbing (one Show flag per dialog) and the settings dialog.</summary>
public partial class MainViewModel
{
    /// <summary>Opens a native open-file dialog (multi-select); returns the picked .maFile paths.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickMaFiles { get; set; }
    /// <summary>Opens a native save-file dialog with a suggested name, returns the path (or null).</summary>
    public Func<string, Task<string?>>? PickExportPath { get; set; }
    /// <summary>Opens a native folder picker (for batch export); returns the folder path (or null).</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private bool _showWizard;
    [ObservableProperty] private bool _showImport;
    [ObservableProperty] private bool _showExport;
    [ObservableProperty] private bool _showLogin;
    [ObservableProperty] private bool _showRemove;
    [ObservableProperty] private bool _showSetPasskey;
    [ObservableProperty] private bool _showBatchRemove;
    [ObservableProperty] private bool _showTransfer;
    [ObservableProperty] private bool _showOfferDetails;
    [ObservableProperty] private bool _showDeactivate;
    [ObservableProperty] private bool _showInventory;

    public bool AnyDialogOpen => ShowSettings || ShowWizard || ShowImport || ShowExport || ShowLogin || ShowRemove || ShowSetPasskey || ShowBatchRemove || ShowTransfer || ShowOfferDetails || ShowDeactivate || ShowGroupPicker || ShowLoginDetail || ShowCs2Sync || ShowInventory;

    partial void OnShowSettingsChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowWizardChanged(bool value)
    {
        OnPropertyChanged(nameof(AnyDialogOpen));
        OnPropertyChanged(nameof(WizardBodyVisible));
    }
    partial void OnShowImportChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowExportChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowLoginChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowRemoveChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowSetPasskeyChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowBatchRemoveChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowTransferChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowOfferDetailsChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowDeactivateChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowGroupPickerChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowLoginDetailChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowCs2SyncChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));
    partial void OnShowInventoryChanged(bool value) => OnPropertyChanged(nameof(AnyDialogOpen));

    [RelayCommand]
    private void CloseDialogs()
    {
        ShowSettings = ShowWizard = ShowImport = ShowExport = ShowLogin = ShowRemove = ShowSetPasskey = ShowBatchRemove = ShowTransfer = ShowOfferDetails = ShowDeactivate = ShowGroupPicker = ShowLoginDetail = ShowCs2Sync = ShowInventory = false;
    }
}
