using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.Core.Update;

namespace Vouch.App.ViewModels;

/// <summary>Checks GitHub for a newer release (tags in <c>v0.0.0</c> form). Silent on startup —
/// only a real update surfaces (a toast); Settings also offers a manual check + a link to the release.</summary>
public partial class MainViewModel
{
    private const string RepoOwner = "Monodesu";
    private const string RepoName = "Vouch";

    private readonly UpdateChecker _updater = new();
    private string _latestUrl = "";

    /// <summary>This build's version, e.g. "0.1.0".</summary>
    public string AppVersion { get; } = CurrentVersion().ToString(3);

    [ObservableProperty] private string _updateCheckStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _latestVersion = "";

    private static Version CurrentVersion() =>
        typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Background check on startup — sets the update state and toasts only if newer exists.</summary>
    public async Task CheckForUpdatesOnStartup()
    {
        var latest = await _updater.FetchLatestAsync(RepoOwner, RepoName);
        if (latest is null || !UpdateChecker.IsNewer(latest.Version, CurrentVersion())) return;

        SetUpdateAvailable(latest);
        ShowToast(Loc.T("Update_ToastAvailable", latest.Tag), ToastKind.Info);
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        UpdateCheckStatus = Loc.T("Update_Checking");
        var latest = await _updater.FetchLatestAsync(RepoOwner, RepoName);
        if (latest is null)
        {
            UpdateCheckStatus = Loc.T("Update_CheckFailed");
            return;
        }

        if (UpdateChecker.IsNewer(latest.Version, CurrentVersion()))
        {
            SetUpdateAvailable(latest);
            UpdateCheckStatus = Loc.T("Update_Available", latest.Tag);
        }
        else
        {
            UpdateAvailable = false;
            UpdateCheckStatus = Loc.T("Update_UpToDate");
        }
    }

    private void SetUpdateAvailable(ReleaseInfo latest)
    {
        UpdateAvailable = true;
        LatestVersion = latest.Tag;
        _latestUrl = latest.Url;
    }

    [RelayCommand]
    private async Task OpenRelease()
    {
        if (!string.IsNullOrEmpty(_latestUrl) && OpenUrl is not null)
            await OpenUrl(_latestUrl);
    }
}
