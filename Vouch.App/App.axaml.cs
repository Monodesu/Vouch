using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Vouch.App.ViewModels;
using Vouch.App.Views;

namespace Vouch.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };

            // A second launch pings us over the pipe — bring the window back to the front.
            Platform.SingleInstance.StartServer(() => Dispatcher.UIThread.Post(ShowMainWindow));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e) => ShowMainWindow();

    private void TrayShow_Click(object? sender, EventArgs e) => ShowMainWindow();

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is MainWindow mw) mw.AllowClose = true;
            desktop.Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } w)
        {
            w.Show();
            w.WindowState = WindowState.Normal;
            w.Activate();
        }
    }
}
