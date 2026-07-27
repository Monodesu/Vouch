using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Vouch.App.Platform;

/// <summary>
/// Launches a Chromium-family browser (Chrome / Edge / Brave / Chromium) in a throwaway profile, injects
/// a cookie over the DevTools protocol, and navigates it. This hands a logged-in Steam web session to a
/// real, isolated browser window without touching the user's own browser data — used to reach Steam's
/// web-only "sign out of all devices". Cross-platform; needs a Chromium-based browser installed.
/// </summary>
public static class CdpBrowser
{
    /// <summary>True when a supported browser was found on this machine.</summary>
    public static bool IsAvailable => FindBrowser() is not null;

    private const string ProfilePrefix = "vouch-browser-";

    /// <summary>Opens <paramref name="navigateUrl"/> in a fresh isolated browser window with
    /// <paramref name="cookieValue"/> set as <c>steamLoginSecure</c> for <c>.steampowered.com</c>.</summary>
    public static async Task LaunchWithCookieAsync(string cookieValue, string navigateUrl, CancellationToken ct = default)
    {
        var exe = FindBrowser() ?? throw new InvalidOperationException("No Chromium-based browser found.");
        CleanStaleProfiles();

        int port = FreePort();
        var profile = Path.Combine(Path.GetTempPath(), ProfilePrefix + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(profile);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[]
        {
            $"--user-data-dir={profile}", $"--remote-debugging-port={port}",
            // Chrome 111+ rejects DevTools WebSocket connections without an allowed origin.
            "--remote-allow-origins=*",
            "--no-first-run", "--no-default-browser-check", "--new-window", "about:blank",
        }) psi.ArgumentList.Add(a);
        Process.Start(psi);

        var wsUrl = await WaitForDevToolsAsync(port, ct)
            ?? throw new InvalidOperationException("Browser DevTools endpoint didn't come up.");

        using var sock = new ClientWebSocket();
        await sock.ConnectAsync(new Uri(wsUrl), ct);

        long expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        await SendAsync(sock, 1, "Network.setCookie", new
        {
            name = "steamLoginSecure",
            value = cookieValue,
            domain = ".steampowered.com",
            path = "/",
            secure = true,
            httpOnly = true,
            sameSite = "None",
            expires,
        }, ct);
        await SendAsync(sock, 2, "Page.navigate", new { url = navigateUrl }, ct);
        await Task.Delay(300, ct); // let the navigation start before we drop the socket
        try { await sock.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { }
    }

    private static async Task<string?> WaitForDevToolsAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json", ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                    if (target.TryGetProperty("type", out var ty) && ty.GetString() == "page"
                        && target.TryGetProperty("webSocketDebuggerUrl", out var ws))
                        return ws.GetString();
            }
            catch { /* not up yet */ }
            await Task.Delay(150, ct);
        }
        return null;
    }

    private static async Task SendAsync(ClientWebSocket sock, int id, string method, object prms, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { id, method, @params = prms });
        await sock.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
        var buf = new byte[16384];
        try { await sock.ReceiveAsync(buf, ct); } catch { /* best-effort ack */ }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Best-effort removal of temp profiles from earlier runs (skips any still in use).</summary>
    private static void CleanStaleProfiles()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), ProfilePrefix + "*"))
                try { Directory.Delete(dir, recursive: true); } catch { /* still open — leave it */ }
        }
        catch { /* ignore */ }
    }

    private static string? FindBrowser()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.AddRange(new[]
            {
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
                Path.Combine(pf86, @"BraveSoftware\Brave-Browser\Application\brave.exe"),
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.AddRange(new[]
            {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
            });
        }
        else
        {
            foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium",
                                         "chromium-browser", "microsoft-edge", "brave-browser" })
                if (Which(name) is { } path) candidates.Add(path);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? Which(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("which", name) { RedirectStandardOutput = true, UseShellExecute = false };
            using var pr = Process.Start(psi);
            if (pr is null) return null;
            string outp = pr.StandardOutput.ReadToEnd().Trim();
            pr.WaitForExit(2000);
            return outp.Length > 0 && File.Exists(outp) ? outp : null;
        }
        catch { return null; }
    }
}
