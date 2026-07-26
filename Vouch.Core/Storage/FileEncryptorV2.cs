using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;

namespace Vouch.Core.Storage;

/// <summary>
/// The v2 encrypted-maFile format: a self-contained JSON envelope holding the KDF
/// parameters, salt, nonce and AES-256-GCM ciphertext+tag — no companion manifest needed.
/// Argon2id (memory-hard) replaces v1's PBKDF2-SHA1; GCM's auth tag detects both wrong
/// passkeys and tampering. v1 files remain readable via <see cref="FileEncryptor"/>;
/// all new writes use this format.
/// </summary>
public static class FileEncryptorV2
{
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    // RFC 9106 "moderate" cost; stored per-file so they can be raised later.
    private const int DefaultMemoryKib = 65536; // 64 MB
    private const int DefaultIterations = 3;
    private const int DefaultParallelism = 4;

    // Ceilings when reading a file's stored params — a crafted envelope must not be
    // able to turn decryption into a denial of service.
    private const int MaxMemoryKib = 1 << 19; // 512 MB
    private const int MaxIterations = 16;
    private const int MaxParallelism = 16;

    public sealed class Envelope
    {
        [JsonPropertyName("v")] public int Version { get; set; }
        [JsonPropertyName("kdf")] public string Kdf { get; set; } = "argon2id";
        [JsonPropertyName("m")] public int MemoryKib { get; set; }
        [JsonPropertyName("t")] public int Iterations { get; set; }
        [JsonPropertyName("p")] public int Parallelism { get; set; }
        [JsonPropertyName("salt")] public string Salt { get; set; } = "";
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
        [JsonPropertyName("data")] public string Data { get; set; } = "";
    }

    /// <summary>True when the text parses as a v2 envelope (vs plaintext maFile JSON or a v1 blob).</summary>
    public static bool LooksLikeV2(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("v", out var v)
                && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 2;
        }
        catch (JsonException) { return false; }
    }

    public static string Encrypt(string passkey, string plaintext)
    {
        if (string.IsNullOrEmpty(passkey)) throw new ArgumentException("Passkey is empty.", nameof(passkey));

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var key = DeriveKey(passkey, salt, DefaultMemoryKib, DefaultIterations, DefaultParallelism);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(key, TagLength))
            aes.Encrypt(nonce, plain, cipher, tag);

        return JsonSerializer.Serialize(new Envelope
        {
            Version = 2,
            MemoryKib = DefaultMemoryKib,
            Iterations = DefaultIterations,
            Parallelism = DefaultParallelism,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Data = Convert.ToBase64String(cipher),
        });
    }

    /// <summary>Returns the plaintext, or null when the passkey is wrong or the file was tampered with.</summary>
    public static string? Decrypt(string passkey, string envelopeText)
    {
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(envelopeText); }
        catch (JsonException) { return null; }
        if (env is not { Version: 2 } || env.Kdf != "argon2id") return null;
        if (env.MemoryKib is <= 0 or > MaxMemoryKib) return null;
        if (env.Iterations is <= 0 or > MaxIterations) return null;
        if (env.Parallelism is <= 0 or > MaxParallelism) return null;

        try
        {
            var salt = Convert.FromBase64String(env.Salt);
            var nonce = Convert.FromBase64String(env.Nonce);
            var tag = Convert.FromBase64String(env.Tag);
            var cipher = Convert.FromBase64String(env.Data);
            var key = DeriveKey(passkey, salt, env.MemoryKib, env.Iterations, env.Parallelism);

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
        catch (CryptographicException) { return null; } // wrong passkey or tampered (tag mismatch)
    }

    private static byte[] DeriveKey(string passkey, byte[] salt, int memoryKib, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(passkey))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(KeyLength);
    }
}
