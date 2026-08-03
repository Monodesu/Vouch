using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Vouch.App.ViewModels;

namespace Vouch.App.Views;

public partial class MainWindow : Window
{
    /// <summary>Set by the tray "Exit" so OnClosing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    /// <summary>OnOpened fires again every time the window is re-shown from the tray, so the
    /// "start minimized" hide must only run on the very first open — otherwise every manual
    /// re-show immediately hides again (an unbreakable loop).</summary>
    private bool _firstOpenHandled;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;

            vm.ClipboardCopy = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            vm.OpenUrl = url => Launcher.LaunchUriAsync(new Uri(url));
            vm.GetWindowHandle = () => TryGetPlatformHandle()?.Handle ?? nint.Zero;

            vm.PickMaFiles = async () =>
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import maFile",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Steam maFile") { Patterns = new[] { "*.maFile", "*.json" } },
                        FilePickerFileTypes.All
                    }
                });
                return files.Select(f => f.TryGetLocalPath())
                            .Where(p => !string.IsNullOrEmpty(p))
                            .Cast<string>()
                            .ToList();
            };

            vm.PickExportPath = async suggested =>
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export maFile",
                    SuggestedFileName = suggested,
                    DefaultExtension = "maFile"
                });
                return file?.TryGetLocalPath();
            };

            vm.PickFolder = async () =>
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Export selected maFiles to folder",
                    AllowMultiple = false
                });
                return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            };

            // Theme setting drives the actual window variant.
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Theme)) ApplyTheme(vm.Theme);
            };
            ApplyTheme(vm.Theme);
        };
    }

    // Pause + refill the toast countdown while the pointer is over it.
    private void Toast_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HoverToast(true);
    }

    private void Toast_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.HoverToast(false);
    }

    private void ApplyTheme(string theme) => RequestedThemeVariant = theme switch
    {
        "Light" => ThemeVariant.Light,
        "System" => ThemeVariant.Default,
        _ => ThemeVariant.Dark
    };

    // ---- custom title bar ----

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeRestore_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(); // OnClosing may hide to tray

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "❐" : "▢";
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && DataContext is MainViewModel { MinimizeToTray: true })
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Test/dev capture path (VOUCH_SHOT set) — see MainWindow.ScreenshotHarness.cs.
        if (ScreenshotRequested)
        {
            RunScreenshotHarness();
            return;
        }

        if (_firstOpenHandled) return; // re-shown from the tray — don't re-run startup behaviour
        _firstOpenHandled = true;

        if (DataContext is MainViewModel vm)
        {
            _ = vm.CheckForUpdatesOnStartup(); // background; toasts only if a newer release exists
            if (vm.StartMinimized) Hide();     // straight to the tray
        }
    }
}
