using System;
using System.IO;
using System.Runtime.Versioning;

namespace Vouch.App.Platform;

/// <summary>
/// Launch-on-boot via a shortcut in the user's Startup folder
/// (…\Start Menu\Programs\Startup\Vouch.lnk). No-op on non-Windows. The presence
/// of the shortcut is the source of truth, so the toggle reflects reality even if
/// the user deleted it manually.
/// </summary>
public static class Autostart
{
    /// <summary>Only Windows is supported; the settings toggle hides elsewhere.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    private static string LnkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Vouch.lnk");

    public static bool IsEnabled() => OperatingSystem.IsWindows() && File.Exists(LnkPath);

    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (enabled) CreateShortcut();
            else File.Delete(LnkPath);
        }
        catch { /* couldn't write the Startup folder — ignore */ }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic sc = shell.CreateShortcut(LnkPath);
        sc.TargetPath = exe;
        sc.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
        sc.Description = "Vouch";
        sc.Save();
    }
}
