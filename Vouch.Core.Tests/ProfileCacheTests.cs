using Vouch.Core.Storage;

namespace Vouch.Core.Tests;

public class ProfileCacheTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vouch_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsProfileAndAvatar()
    {
        var dir = TempDir();
        try
        {
            var cache = new ProfileCache(dir);
            var avatar = new byte[] { 1, 2, 3, 4, 5 };
            cache.Save(76561198000000090, new CachedProfile
            {
                PersonaName = "Rabscuttle",
                VacBans = 0,
                GameBans = 2,
                TradeBanned = true,
                UpdatedAt = 1700000000,
            }, avatar);

            var (profile, bytes) = new ProfileCache(dir).Load(76561198000000090);
            Assert.NotNull(profile);
            Assert.Equal("Rabscuttle", profile!.PersonaName);
            Assert.Equal(0, profile.VacBans);
            Assert.Equal(2, profile.GameBans);
            Assert.True(profile.TradeBanned);
            Assert.Equal(avatar, bytes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_Missing_ReturnsNulls()
    {
        var dir = TempDir();
        try
        {
            var (profile, avatar) = new ProfileCache(dir).Load(1);
            Assert.Null(profile);
            Assert.Null(avatar);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Delete_RemovesBothFiles()
    {
        var dir = TempDir();
        try
        {
            var cache = new ProfileCache(dir);
            cache.Save(42, new CachedProfile { PersonaName = "x" }, new byte[] { 9 });
            cache.Delete(42);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}
