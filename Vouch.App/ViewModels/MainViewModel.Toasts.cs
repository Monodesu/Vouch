using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Vouch.App.ViewModels;

/// <summary>Severity of a transient toast — picks its color in the banner.</summary>
public enum ToastKind { Success, Error, Info }

/// <summary>
/// A small transient toast banner, rendered in MainWindow's normal visual tree (not the overlay
/// layer, so it's reliable under the custom window chrome). One toast at a time; a newer toast
/// replaces the current one. A countdown bar depletes over the lifetime and auto-dismisses at zero;
/// hovering the toast refills the bar and pauses it, so it stays while you read.
/// </summary>
public partial class MainViewModel
{
    private const double ToastTickMs = 25;
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(4);

    private DispatcherTimer? _toastTimer;
    private bool _toastHovered;

    [ObservableProperty] private bool _toastVisible;
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private bool _toastIsError;
    [ObservableProperty] private double _toastProgress = 1; // remaining fraction, 1 = full → 0 = dismiss

    public void ShowToast(string message, ToastKind kind = ToastKind.Success)
    {
        ToastMessage = message;
        ToastIsError = kind == ToastKind.Error;
        ToastVisible = true;
        ToastProgress = 1;
        _toastHovered = false;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ToastTickMs) };
        _toastTimer.Tick += (_, _) =>
        {
            if (_toastHovered) return; // paused while the pointer is over it
            ToastProgress -= ToastTickMs / ToastDuration.TotalMilliseconds;
            if (ToastProgress <= 0) DismissToast();
        };
        _toastTimer.Start();
    }

    /// <summary>Pointer over the toast: refill the bar and hold; off: resume the countdown from full.</summary>
    public void HoverToast(bool hovering)
    {
        _toastHovered = hovering;
        if (hovering) ToastProgress = 1;
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastTimer?.Stop();
        _toastHovered = false;
        ToastVisible = false;
    }
}
