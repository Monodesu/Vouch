using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Vouch.Core.Steam;

/// <summary>Which parts of a CS2 config to copy. Settings = the big convars bucket (crosshair,
/// sensitivity, viewmodel, HUD, radar, audio…); Keys = key binds; Video = the per-PC graphics file.</summary>
[Flags]
public enum Cs2Parts
{
    None = 0,
    Settings = 1,
    Keys = 2,
    Video = 4,
}

/// <summary>Result of syncing to one target account.</summary>
public record Cs2SyncOutcome(ulong SteamId, uint AccountId, bool Ok, string Note);

/// <summary>
/// Copies a source account's CS2 config files onto other accounts, so alts on the same PC share the
/// main's crosshair / binds / settings. Pure file operations under a Steam <c>userdata</c> directory
/// (<c>userdata/&lt;accountid&gt;/730/local/cfg/</c>); no Steam login or API. We overwrite only the local
/// working <c>.vcfg</c> files and leave each target's cloud state (<c>remotecache.vdf</c>, <c>*_lastclouded</c>)
/// alone, so Steam sees the local files as changed and uploads them to that account's cloud on next login.
/// </summary>
public class Cs2ConfigSync
{
    private const ulong SteamId64Base = 76561197960265728UL;
    private const string ConvarsFile = "cs2_user_convars_0_slot0.vcfg";
    private const string KeysFile0 = "cs2_user_keys_0_slot0.vcfg";
    private const string VideoFile = "cs2_video.txt";

    public string UserdataDir { get; }

    public Cs2ConfigSync(string userdataDir) => UserdataDir = userdataDir;

    /// <summary>The 32-bit Steam account id (the <c>userdata</c> folder name) for a SteamID64.</summary>
    public static uint AccountId(ulong steamId64) => (uint)(steamId64 - SteamId64Base);

    private string CfgDir(ulong steamId64) =>
        Path.Combine(UserdataDir, AccountId(steamId64).ToString(), "730", "local", "cfg");

    private static IEnumerable<string> FilesFor(Cs2Parts parts)
    {
        if (parts.HasFlag(Cs2Parts.Settings)) yield return ConvarsFile;
        if (parts.HasFlag(Cs2Parts.Keys))
        {
            yield return KeysFile0;
            yield return "cs2_user_keys_0_slot1.vcfg";
            yield return "cs2_user_keys_0_slot2.vcfg";
            yield return "cs2_user_keys_0_slot3.vcfg";
        }
        if (parts.HasFlag(Cs2Parts.Video)) yield return VideoFile;
    }

    /// <summary>True if this account has a CS2 (730) cfg folder on disk (i.e. it has run CS2 here).</summary>
    public bool HasCs2(ulong steamId64) => Directory.Exists(CfgDir(steamId64));

    /// <summary>Which parts the account actually has files for — so the UI can disable missing ones.</summary>
    public Cs2Parts AvailableParts(ulong steamId64)
    {
        var dir = CfgDir(steamId64);
        var parts = Cs2Parts.None;
        if (File.Exists(Path.Combine(dir, ConvarsFile))) parts |= Cs2Parts.Settings;
        if (File.Exists(Path.Combine(dir, KeysFile0))) parts |= Cs2Parts.Keys;
        if (File.Exists(Path.Combine(dir, VideoFile))) parts |= Cs2Parts.Video;
        return parts;
    }

    /// <summary>
    /// Copies the selected files from <paramref name="source"/> onto each target. Each target's current
    /// files are backed up under <paramref name="backupRoot"/> first (when given). The source is skipped
    /// if it appears in <paramref name="targets"/>. Returns a per-target outcome.
    /// </summary>
    public IReadOnlyList<Cs2SyncOutcome> Sync(
        ulong source, IReadOnlyList<ulong> targets, Cs2Parts parts, string? backupRoot)
    {
        var results = new List<Cs2SyncOutcome>();
        var srcDir = CfgDir(source);
        var files = FilesFor(parts).Where(f => File.Exists(Path.Combine(srcDir, f))).ToList();
        if (files.Count == 0) return results; // source has nothing to copy for these parts

        foreach (var target in targets)
        {
            if (target == source) continue;
            var tgtDir = CfgDir(target);
            try
            {
                Directory.CreateDirectory(tgtDir);
                if (backupRoot is not null) Backup(target, tgtDir, files, backupRoot);

                foreach (var f in files)
                {
                    var dest = Path.Combine(tgtDir, f);
                    File.Copy(Path.Combine(srcDir, f), dest, overwrite: true);
                    File.SetLastWriteTimeUtc(dest, DateTime.UtcNow); // newest → Steam uploads local, not download
                }
                results.Add(new(target, AccountId(target), true, "ok"));
            }
            catch (Exception ex)
            {
                results.Add(new(target, AccountId(target), false, ex.Message));
            }
        }
        return results;
    }

    /// <summary>A saved backup: one timestamped folder holding per-account config snapshots.</summary>
    public record Cs2BackupSet(string Dir, string Stamp, IReadOnlyList<uint> AccountIds);

    /// <summary>Lists the backup sets under <paramref name="backupsRoot"/>, newest first.</summary>
    public static IReadOnlyList<Cs2BackupSet> ListBackups(string backupsRoot)
    {
        var list = new List<Cs2BackupSet>();
        if (!Directory.Exists(backupsRoot)) return list;
        foreach (var stampDir in Directory.EnumerateDirectories(backupsRoot).OrderByDescending(d => d))
        {
            var ids = Directory.EnumerateDirectories(stampDir)
                .Select(d => uint.TryParse(Path.GetFileName(d), out var id) ? id : 0u)
                .Where(id => id != 0).ToList();
            if (ids.Count > 0) list.Add(new Cs2BackupSet(stampDir, Path.GetFileName(stampDir), ids));
        }
        return list;
    }

    /// <summary>Restores one account's config from a backup set folder back into its live cfg dir.</summary>
    public Cs2SyncOutcome Restore(string backupStampDir, ulong steamId)
    {
        var acct = AccountId(steamId);
        var src = Path.Combine(backupStampDir, acct.ToString());
        try
        {
            if (!Directory.Exists(src)) return new(steamId, acct, false, "no backup for this account");
            var dst = CfgDir(steamId);
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.EnumerateFiles(src))
            {
                var dest = Path.Combine(dst, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, DateTime.UtcNow);
            }
            return new(steamId, acct, true, "ok");
        }
        catch (Exception ex) { return new(steamId, acct, false, ex.Message); }
    }

    private void Backup(ulong steamId, string tgtDir, IEnumerable<string> files, string backupRoot)
    {
        var dest = Path.Combine(backupRoot, AccountId(steamId).ToString());
        Directory.CreateDirectory(dest);
        foreach (var f in files)
        {
            var src = Path.Combine(tgtDir, f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dest, f), overwrite: true);
        }
    }
}
