using System.Security.Cryptography;
using Vouch.Core.Steam;
using Vouch.Core.Storage;

namespace Vouch.Core.Tests;

public class MaFileRepositoryTests
{
    private static SteamGuardAccount Account(ulong steamId) => new()
    {
        SharedSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
        AccountName = $"user_{steamId}",
        Session = new SessionData { SteamId = steamId }
    };

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouch_repo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Isolate the encrypted-flag store (app settings) per test dir.
    private static MaFileRepository Repo(string dir) => new(dir, Path.Combine(dir, "settings.json"));

    [Fact]
    public void Save_ThenLoad_PersistsAccounts()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000010);
            var b = Account(76561198000000011);
            repo.Save(a);
            repo.Save(b);

            // fresh repo over the same directory = simulates a restart
            var loaded = Repo(dir).LoadAll();
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, x => x.Session!.SteamId == a.Session!.SteamId && x.SharedSecret == a.SharedSecret);
            Assert.Contains(loaded, x => x.Session!.SteamId == b.Session!.SteamId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_SameAccountTwice_DoesNotDuplicate()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000020);
            repo.Save(a);
            a.AccountName = "renamed";
            repo.Save(a);

            var loaded = repo.LoadAll();
            Assert.Single(loaded);
            Assert.Equal("renamed", loaded[0].AccountName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EnableEncryption_RoundTrips_AndLocksOutWrongPasskey()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000040);
            var b = Account(76561198000000041);
            repo.Save(a);
            repo.Save(b);

            repo.EnableEncryption("hunter2");

            // On disk: no longer parseable JSON.
            var raw = File.ReadAllText(Path.Combine(dir, "76561198000000040.maFile"));
            Assert.DoesNotContain("shared_secret", raw);

            // Restart: locked until the right passkey is given.
            var fresh = Repo(dir);
            Assert.True(fresh.IsEncrypted);
            Assert.True(fresh.RequiresPasskey);
            Assert.Empty(fresh.LoadAll());
            Assert.False(fresh.TryUnlock("wrong"));
            Assert.True(fresh.TryUnlock("hunter2"));

            var loaded = fresh.LoadAll();
            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, x => x.Session!.SteamId == a.Session!.SteamId && x.SharedSecret == a.SharedSecret);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_WhenEncrypted_RotatesSaltAndNonce()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000050);
            repo.Save(a);
            repo.EnableEncryption("pw");

            var file = Path.Combine(dir, "76561198000000050.maFile");
            var env1 = System.Text.Json.JsonSerializer.Deserialize<FileEncryptorV2.Envelope>(File.ReadAllText(file))!;
            repo.Save(a);
            var env2 = System.Text.Json.JsonSerializer.Deserialize<FileEncryptorV2.Envelope>(File.ReadAllText(file))!;

            Assert.NotEqual(env1.Salt, env2.Salt);
            Assert.NotEqual(env1.Nonce, env2.Nonce);
            Assert.Single(repo.LoadAll()); // still decryptable after the rewrite
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_WhenLocked_Throws()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000060);
            repo.Save(a);
            repo.EnableEncryption("pw");

            var locked = Repo(dir);
            Assert.Throws<InvalidOperationException>(() => locked.Save(a));
        }
        finally { Directory.Delete(dir, true); }
    }


    [Fact]
    public void DisableEncryption_RestoresPlaintext()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000070);
            repo.Save(a);
            repo.EnableEncryption("pw");
            repo.DisableEncryption();

            var fresh = Repo(dir);
            Assert.False(fresh.IsEncrypted);
            Assert.False(fresh.RequiresPasskey);
            var loaded = fresh.LoadAll();
            Assert.Single(loaded);
            Assert.Equal(a.SharedSecret, loaded[0].SharedSecret);
            Assert.Contains("shared_secret", File.ReadAllText(Path.Combine(dir, "76561198000000070.maFile")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SaveLayout_PersistsOrderAndGroups()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000100);
            var b = Account(76561198000000101);
            repo.Save(a);
            repo.Save(b);

            // Reverse the order, put b in a group, collapse it.
            repo.SaveLayout(
                new[]
                {
                    new MaFileEntry { SteamId = b.Session!.SteamId, Group = "alts" },
                    new MaFileEntry { SteamId = a.Session!.SteamId },
                },
                new[] { new GroupEntry { Name = "alts", Collapsed = true } });

            var index = Repo(dir).GetIndex(); // fresh repo = reload from disk
            Assert.Equal(new[] { b.Session!.SteamId, a.Session!.SteamId }, index.Accounts.Select(e => e.SteamId));
            Assert.Equal("alts", index.Accounts[0].Group);
            Assert.Null(index.Accounts[1].Group);
            Assert.Single(index.Groups);
            Assert.True(index.Groups[0].Collapsed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_PreservesExistingGroupAndPosition()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000110);
            var b = Account(76561198000000111);
            repo.Save(a);
            repo.Save(b);
            repo.SaveLayout(
                new[] { new MaFileEntry { SteamId = a.Session!.SteamId, Group = "g" }, new MaFileEntry { SteamId = b.Session!.SteamId } },
                Array.Empty<GroupEntry>());

            a.AccountName = "renamed";
            repo.Save(a); // re-saving the file must not drop its group or move it

            var index = Repo(dir).GetIndex();
            Assert.Equal(a.Session!.SteamId, index.Accounts[0].SteamId);
            Assert.Equal("g", index.Accounts[0].Group);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Delete_RemovesAccount()
    {
        var dir = TempDir();
        try
        {
            var repo = Repo(dir);
            var a = Account(76561198000000030);
            repo.Save(a);
            repo.Delete(a.Session!.SteamId);

            Assert.Empty(repo.LoadAll());
            Assert.False(File.Exists(Path.Combine(dir, "76561198000000030.maFile")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
