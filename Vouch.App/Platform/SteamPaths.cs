using System;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Vouch.App.Platform;

/// <summary>Locates the local Steam install so features like CS2 config sync can find
/// <c>userdata/</c>. Windows-only (reads the Steam registry key); returns null elsewhere.</summary>
public static class SteamPaths
{
    /// <summary>The Steam <c>userdata</c> directory, or null if Steam isn't found.</summary>
    public static string? UserdataDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return UserdataDirWindows(); } catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static string? UserdataDirWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steamPath || steamPath.Length == 0) return null;
        var userdata = Path.Combine(steamPath.Replace('/', Path.DirectorySeparatorChar), "userdata");
        return Directory.Exists(userdata) ? userdata : null;
    }
}
