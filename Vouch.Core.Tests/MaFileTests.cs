using System.Security.Cryptography;
using Vouch.Core.Steam;
using Vouch.Core.Storage;

namespace Vouch.Core.Tests;

public class MaFileTests
{
    private static SteamGuardAccount SampleAccount() => new()
    {
        SharedSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
        IdentitySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
        AccountName = "test_user",
        RevocationCode = "R12345",
        Session = new SessionData { SteamId = 76561198000000001 }
    };

    [Fact]
    public void LoadFile_ReadsPlaintextMaFile()
    {
        var acc = SampleAccount();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.maFile");
        try
        {
            MaFileStore.ExportPlain(acc, path);
            var result = MaFileStore.LoadFile(path);

            Assert.Equal(MaFileLoadStatus.Ok, result.Status);
            Assert.Equal(acc.SharedSecret, result.Account!.SharedSecret);
            Assert.Equal(76561198000000001UL, result.Account.Session!.SteamId);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadFile_EncryptedWithoutPassword_AsksForPassword_ThenLoads()
    {
        var acc = SampleAccount();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{acc.Session!.SteamId}.maFile");
        try
        {
            MaFileStore.ExportEncrypted(acc, path, "s3cret");

            Assert.Equal(MaFileLoadStatus.NeedsPassword, MaFileStore.LoadFile(path).Status);
            Assert.Equal(MaFileLoadStatus.WrongPassword, MaFileStore.LoadFile(path, "nope").Status);

            var ok = MaFileStore.LoadFile(path, "s3cret");
            Assert.Equal(MaFileLoadStatus.Ok, ok.Status);
            Assert.Equal(acc.SharedSecret, ok.Account!.SharedSecret);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RealCode_FromImportedMaFile_IsGeneratable()
    {
        var acc = SampleAccount();
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.maFile");
        try
        {
            MaFileStore.ExportPlain(acc, path);
            var loaded = MaFileStore.LoadFile(path).Account!;
            var code = SteamGuard.GenerateCode(loaded.SharedSecret!, SteamGuard.CurrentWindow(DateTimeOffset.UtcNow));

            Assert.Equal(5, code.Length);
        }
        finally { File.Delete(path); }
    }
}
