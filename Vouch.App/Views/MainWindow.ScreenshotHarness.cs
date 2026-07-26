using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vouch.App.Localization;
using Vouch.App.ViewModels;

namespace Vouch.App.Views;

/// <summary>
/// Test/dev-only off-screen render harness, driven entirely by <c>VOUCH_*</c> environment variables —
/// never touched by a normal run (it's a no-op unless <c>VOUCH_SHOT</c> is set). It stages a
/// theme/dialog/import, renders the window to a PNG, optionally writes a verification sidecar, and
/// exits. Kept out of <see cref="MainWindow"/>'s production code so that file stays about the window.
/// </summary>
/// <remarks>
/// Recognised vars: VOUCH_SHOT (output PNG, required), VOUCH_THEME, VOUCH_UNLOCK (passkey), VOUCH_IMPORT
/// (maFile to import), VOUCH_DIALOG (which dialog/state to open), VOUCH_UPDATE=1 (real profile fetch),
/// VOUCH_SHOT_DELAY (ms before capture), VOUCH_VERIFY (sidecar path for window/code/secret).
/// </remarks>
public partial class MainWindow
{
    private static bool ScreenshotRequested =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VOUCH_SHOT"));

    private void RunScreenshotHarness()
    {
        // Optional window size override, to exercise responsive layouts (e.g. VOUCH_WIN_H=520).
        if (Environment.GetEnvironmentVariable("VOUCH_WIN_H") is { } h && double.TryParse(h, out var wh)) Height = wh;
        if (Environment.GetEnvironmentVariable("VOUCH_WIN_W") is { } w && double.TryParse(w, out var ww)) Width = ww;

        if (DataContext is MainViewModel vm)
        {
            StageScene(vm);
        }
        if (Environment.GetEnvironmentVariable("VOUCH_DIALOG") == "addmenu")
            Dispatcher.UIThread.Post(OpenAddMenu, DispatcherPriority.Loaded);
        ScheduleCapture(Environment.GetEnvironmentVariable("VOUCH_SHOT")!);
    }

    // Opens the Add-account MenuFlyout so its styling/animation can be exercised off-screen.
    private void OpenAddMenu()
    {
        var button = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Flyout is MenuFlyout);
        if (button?.Flyout is { } flyout) flyout.ShowAt(button);
    }

    /// <summary>Applies theme, unlocks, and opens whatever dialog/state VOUCH_DIALOG asks for.</summary>
    private static void StageScene(MainViewModel vm)
    {
        var theme = Environment.GetEnvironmentVariable("VOUCH_THEME");
        if (!string.IsNullOrEmpty(theme)) vm.Theme = theme;

        // E2E: unlock an encrypted data dir with the given passkey before capturing.
        var unlockKey = Environment.GetEnvironmentVariable("VOUCH_UNLOCK");
        if (!string.IsNullOrEmpty(unlockKey) && vm.ShowUnlock)
        {
            vm.UnlockPasskey = unlockKey;
            vm.UnlockCommand.Execute(null);
        }

        var importPath = Environment.GetEnvironmentVariable("VOUCH_IMPORT");
        if (!string.IsNullOrEmpty(importPath))
        {
            // Real-import path: import an actual maFile so the shot shows its real code.
            vm.ImportFromPath(importPath);
            return;
        }

        // Mock demo: fetched avatars + ban chips (never clobber real accounts' cached data).
        foreach (var acc in vm.Accounts)
            if (!acc.IsReal)
                acc.ApplyFetchedInfo();
        if (vm.Accounts.Count > 2) vm.SelectedAccount = vm.Accounts[2]; // Praetor (banned)

        OpenStagedDialog(vm, Environment.GetEnvironmentVariable("VOUCH_DIALOG"));
    }

    private static void OpenStagedDialog(MainViewModel vm, string? dialog)
    {
        switch (dialog)
        {
            case "settings": vm.ShowSettings = true; break;
            case "wizard": vm.ShowWizard = true; break;
            case "wizard2": vm.ShowWizard = true; vm.WizardStep = 2; break; // phone: enter number
            case "wizard2b": vm.ShowWizard = true; vm.WizardStep = 2; vm.WizardPhoneStage = 1; break; // await email
            case "wizard2c": vm.ShowWizard = true; vm.WizardStep = 2; vm.WizardPhoneStage = 2; break; // verify SMS
            case "wizard3": // confirm (revocation + SMS)
                vm.ShowWizard = true;
                vm.WizardStep = 3;
                vm.WizardRevocationCode = "R48213";
                break;
            case "import": vm.ShowImport = true; break;
            case "remove":
                vm.OpenRemoveCommand.Execute(null);
                vm.RemoveModeAppOnly = false;
                vm.RemoveModeToEmail = true; // show the revocation-code stage
                break;
            case "unlock": vm.ShowUnlock = true; break;
            case "setpasskey": vm.ShowSetPasskey = true; break;
            case "export": vm.ShowExport = true; break;
            case "password-edit": vm.BeginEditPasswordCommand.Execute(null); break;
            case "batchremove": vm.BeginBatchRemove(vm.Accounts.Take(3).ToList()); break;
            case "transfer":
                vm.TradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=123&token=abc";
                vm.ShowTransfer = true;
                break;
            case "transfer-select":
                vm.TradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=123&token=abc";
                vm.TransferSelectMode = true;
                vm.InventoryItems.Add(new TransferItem(new Vouch.Core.Steam.InventoryItem(730, "2", "1", 1, "AK-47 | Redline (Field-Tested)")) { IsSelected = true });
                vm.InventoryItems.Add(new TransferItem(new Vouch.Core.Steam.InventoryItem(730, "2", "2", 1, "AWP | Asiimov (Well-Worn)")));
                vm.InventoryItems.Add(new TransferItem(new Vouch.Core.Steam.InventoryItem(730, "2", "3", 1, "Glock-18 | Water Elemental")) { IsSelected = true });
                vm.InventoryItems.Add(new TransferItem(new Vouch.Core.Steam.InventoryItem(730, "2", "4", 1, "★ Karambit | Doppler")));
                vm.ShowTransfer = true;
                break;
            case "toast": vm.ShowToast(Loc.T("Login_ToastSuccess"), ToastKind.Success); break;
            case "login": vm.OpenLoginCommand.Execute(null); break;
            case "login-batch":
                vm.OpenLoginCommand.Execute(null);
                vm.BatchSignInActive = true;
                vm.BatchSignInProgress = Loc.T("Batch_SignInProgress", 2, 5);
                break;
            case "login-email":
                vm.OpenLoginCommand.Execute(null);
                vm.LoginNeedsEmail = true;
                vm.LoginStatus = Loc.T("Login_StatusEmailSent", "a***@gmail.com");
                break;
        }
    }

    /// <summary>Renders the window to <paramref name="shot"/> after a short settle delay, then exits.</summary>
    private void ScheduleCapture(string shot) => Dispatcher.UIThread.Post(async () =>
    {
        var delay = Environment.GetEnvironmentVariable("VOUCH_SHOT_DELAY") is { } d && int.TryParse(d, out var ms) ? ms : 500;
        await Task.Delay(delay); // let the countdown tick + layout settle

        // Optionally trigger a real profile fetch before capturing.
        if (Environment.GetEnvironmentVariable("VOUCH_UPDATE") == "1" && DataContext is MainViewModel uvm)
            await uvm.UpdateInfoCommand.ExecuteAsync(null);

        var size = new PixelSize((int)ClientSize.Width, (int)ClientSize.Height);
        var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
        rtb.Render(this);
        rtb.Save(shot, new PngBitmapEncoderOptions());

        // E2E verification: write the displayed code + window + secret for an independent recompute.
        var verify = Environment.GetEnvironmentVariable("VOUCH_VERIFY");
        if (!string.IsNullOrEmpty(verify) && DataContext is MainViewModel v && v.SelectedAccount is { } a)
            File.WriteAllText(verify, $"{v.DisplayWindow}\t{v.CurrentCode}\t{Convert.ToBase64String(a.SharedSecret)}");

        AllowClose = true;
        Close();
    }, DispatcherPriority.Background);
}
