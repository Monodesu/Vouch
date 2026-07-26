using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class Cs2ConfigSyncTests
{
    private const ulong Base = 76561197960265728UL;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouch_cs2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CfgDir(string userdata, ulong steamId) =>
        Path.Combine(userdata, Cs2ConfigSync.AccountId(steamId).ToString(), "730", "local", "cfg");

    private static void Write(string userdata, ulong steamId, string file, string content)
    {
        var dir = CfgDir(userdata, steamId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, file), content);
    }

    [Fact]
    public void AccountId_IsLower32Bits()
    {
        Assert.Equal(826628868u, Cs2ConfigSync.AccountId(Base + 826628868UL));
    }

    [Fact]
    public void Sync_CopiesSelectedParts_BacksUp_AndSkipsSource()
    {
        var ud = TempDir();
        try
        {
            ulong src = Base + 100, tgt = Base + 200;
            Write(ud, src, "cs2_user_convars_0_slot0.vcfg", "SOURCE convars");
            Write(ud, src, "cs2_user_keys_0_slot0.vcfg", "SOURCE keys");
            Write(ud, src, "cs2_video.txt", "SOURCE video");
            Write(ud, tgt, "cs2_user_convars_0_slot0.vcfg", "OLD target convars");
            Write(ud, tgt, "cs2_user_keys_0_slot0.vcfg", "OLD target keys");
            Write(ud, tgt, "cs2_video.txt", "OLD target video");

            var backup = TempDir();
            var sync = new Cs2ConfigSync(ud);
            var results = sync.Sync(src, new[] { tgt, src }, Cs2Parts.Settings | Cs2Parts.Keys, backup);

            // source is skipped, only the target reported
            Assert.Single(results);
            Assert.True(results[0].Ok);
            Assert.Equal(tgt, results[0].SteamId);

            var tgtDir = CfgDir(ud, tgt);
            Assert.Equal("SOURCE convars", File.ReadAllText(Path.Combine(tgtDir, "cs2_user_convars_0_slot0.vcfg")));
            Assert.Equal("SOURCE keys", File.ReadAllText(Path.Combine(tgtDir, "cs2_user_keys_0_slot0.vcfg")));
            // video was NOT selected -> left untouched
            Assert.Equal("OLD target video", File.ReadAllText(Path.Combine(tgtDir, "cs2_video.txt")));

            // backup captured the target's ORIGINAL convars/keys
            var bDir = Path.Combine(backup, Cs2ConfigSync.AccountId(tgt).ToString());
            Assert.Equal("OLD target convars", File.ReadAllText(Path.Combine(bDir, "cs2_user_convars_0_slot0.vcfg")));
            Assert.Equal("OLD target keys", File.ReadAllText(Path.Combine(bDir, "cs2_user_keys_0_slot0.vcfg")));
        }
        finally { Directory.Delete(ud, true); }
    }

    [Fact]
    public void Sync_CreatesMissingTargetFolder()
    {
        var ud = TempDir();
        try
        {
            ulong src = Base + 100, tgt = Base + 300; // tgt has no CS2 folder yet
            Write(ud, src, "cs2_user_convars_0_slot0.vcfg", "SOURCE convars");

            var sync = new Cs2ConfigSync(ud);
            Assert.False(sync.HasCs2(tgt));
            var results = sync.Sync(src, new[] { tgt }, Cs2Parts.Settings, backupRoot: null);

            Assert.True(results[0].Ok);
            Assert.True(sync.HasCs2(tgt));
            Assert.Equal("SOURCE convars", File.ReadAllText(Path.Combine(CfgDir(ud, tgt), "cs2_user_convars_0_slot0.vcfg")));
        }
        finally { Directory.Delete(ud, true); }
    }

    [Fact]
    public void AvailableParts_ReflectsPresentFiles()
    {
        var ud = TempDir();
        try
        {
            ulong id = Base + 100;
            Write(ud, id, "cs2_user_convars_0_slot0.vcfg", "x");
            Write(ud, id, "cs2_video.txt", "y");
            var sync = new Cs2ConfigSync(ud);
            var parts = sync.AvailableParts(id);
            Assert.True(parts.HasFlag(Cs2Parts.Settings));
            Assert.True(parts.HasFlag(Cs2Parts.Video));
            Assert.False(parts.HasFlag(Cs2Parts.Keys));
        }
        finally { Directory.Delete(ud, true); }
    }
}
