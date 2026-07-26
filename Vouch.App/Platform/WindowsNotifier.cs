using System;
using System.Runtime.InteropServices;

namespace Vouch.App.Platform;

/// <summary>
/// Shows a Windows system notification (tray balloon → rendered as a toast on Win10/11) via
/// Shell_NotifyIcon — no packaging, AUMID, or extra dependencies. A single hidden tray entry is
/// registered lazily and reused; the balloon still shows even though the icon itself stays hidden.
/// No-op on non-Windows.
/// </summary>
public static class WindowsNotifier
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    private const uint NIM_ADD = 0, NIM_MODIFY = 1;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_STATE = 0x08, NIF_INFO = 0x10;
    private const uint NIS_HIDDEN = 0x01;
    private const uint NIIF_INFO = 0x01;
    private const uint TrayId = 0x53DA; // stable per-process tray id
    private static readonly IntPtr IDI_APPLICATION = 32512;

    private static bool _registered;
    private static IntPtr _hwnd;

    /// <summary>Shows a balloon/toast titled <paramref name="title"/> with body <paramref name="body"/>.</summary>
    public static void Notify(IntPtr hwnd, string title, string body)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = hwnd,
                uID = TrayId,
                szTip = "Vouch",
                szInfo = body ?? "",
                szInfoTitle = title ?? "",
                dwInfoFlags = NIIF_INFO,
            };

            if (!_registered || _hwnd != hwnd)
            {
                data.uFlags = NIF_ICON | NIF_TIP | NIF_STATE;
                data.dwState = NIS_HIDDEN;
                data.dwStateMask = NIS_HIDDEN;
                data.hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
                Shell_NotifyIcon(NIM_ADD, ref data);
                _registered = true;
                _hwnd = hwnd;
            }

            data.uFlags = NIF_INFO;
            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch { /* never let a notification failure bubble up */ }
    }
}
