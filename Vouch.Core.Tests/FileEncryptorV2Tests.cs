using System.Text.Json;
using Vouch.Core.Storage;

namespace Vouch.Core.Tests;

public class FileEncryptorV2Tests
{
    [Fact]
    public void Encrypt_RoundTrips()
    {
        var env = FileEncryptorV2.Encrypt("hunter2", """{"shared_secret":"abc"}""");
        Assert.True(FileEncryptorV2.LooksLikeV2(env));
        Assert.Equal("""{"shared_secret":"abc"}""", FileEncryptorV2.Decrypt("hunter2", env));
    }

    [Fact]
    public void Decrypt_WrongPasskey_ReturnsNull()
    {
        var env = FileEncryptorV2.Encrypt("right", "{\"a\":1}");
        Assert.Null(FileEncryptorV2.Decrypt("wrong", env));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ReturnsNull()
    {
        var env = FileEncryptorV2.Encrypt("pw", """{"shared_secret":"payload-to-protect"}""");

        // Flip one byte of the ciphertext — GCM's tag must reject it.
        var doc = JsonSerializer.Deserialize<FileEncryptorV2.Envelope>(env)!;
        var data = Convert.FromBase64String(doc.Data);
        data[0] ^= 0xFF;
        doc.Data = Convert.ToBase64String(data);
        var tampered = JsonSerializer.Serialize(doc);

        Assert.Null(FileEncryptorV2.Decrypt("pw", tampered));
    }

    [Fact]
    public void Decrypt_AbsurdKdfParams_Rejected()
    {
        // A crafted file must not be able to demand gigabytes of KDF memory.
        var env = FileEncryptorV2.Encrypt("pw", "{\"a\":1}");
        var doc = JsonSerializer.Deserialize<FileEncryptorV2.Envelope>(env)!;
        doc.MemoryKib = int.MaxValue;
        Assert.Null(FileEncryptorV2.Decrypt("pw", JsonSerializer.Serialize(doc)));
    }

    [Fact]
    public void Envelopes_UseFreshSaltAndNonce()
    {
        var a = JsonSerializer.Deserialize<FileEncryptorV2.Envelope>(FileEncryptorV2.Encrypt("pw", "{\"a\":1}"))!;
        var b = JsonSerializer.Deserialize<FileEncryptorV2.Envelope>(FileEncryptorV2.Encrypt("pw", "{\"a\":1}"))!;
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Nonce, b.Nonce);
    }

    [Fact]
    public void LooksLikeV2_RejectsPlaintextAndV1Blobs()
    {
        Assert.False(FileEncryptorV2.LooksLikeV2("""{"shared_secret":"abc"}"""));
        Assert.False(FileEncryptorV2.LooksLikeV2("bm90IGpzb24gYXQgYWxs")); // v1-style base64 blob
    }
}
