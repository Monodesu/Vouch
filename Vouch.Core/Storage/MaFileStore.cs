using System.Text.Json;
using Vouch.Core.Steam;

namespace Vouch.Core.Storage;

public enum MaFileLoadStatus
{
    Ok,
    NeedsPassword,
    WrongPassword,
    Invalid
}

public readonly record struct MaFileLoadResult(
    MaFileLoadStatus Status,
    SteamGuardAccount? Account = null,
    string? Error = null);

/// <summary>Reads and writes Steam <c>.maFile</c>s (plaintext or encrypted).</summary>
public static class MaFileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>True when the text is not JSON — i.e. an encrypted base64 blob.</summary>
    private static bool TryParseAccount(string text, out SteamGuardAccount? account)
    {
        account = null;
        try
        {
            var acc = JsonSerializer.Deserialize<SteamGuardAccount>(text, Json);
            if (acc is null || string.IsNullOrEmpty(acc.SharedSecret)) return false;
            account = acc;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads a single <c>.maFile</c>. Plaintext files parse directly; encrypted files are v2
    /// self-contained envelopes and need the <paramref name="password"/>.
    /// </summary>
    public static MaFileLoadResult LoadFile(string path, string? password = null)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return new(MaFileLoadStatus.Invalid, Error: ex.Message); }

        if (TryParseAccount(text, out var plain))
            return new(MaFileLoadStatus.Ok, plain);

        // v2 envelope: self-contained (salt/nonce/params inside the file).
        if (FileEncryptorV2.LooksLikeV2(text))
        {
            if (string.IsNullOrEmpty(password))
                return new(MaFileLoadStatus.NeedsPassword);
            var plainV2 = FileEncryptorV2.Decrypt(password, text);
            return plainV2 is not null && TryParseAccount(plainV2, out var accV2)
                ? new(MaFileLoadStatus.Ok, accV2)
                : new(MaFileLoadStatus.WrongPassword);
        }

        return new(MaFileLoadStatus.Invalid,
            Error: "Unrecognized maFile format (expected plaintext JSON or a v2 encrypted envelope).");
    }

    public static string Serialize(SteamGuardAccount account) => JsonSerializer.Serialize(account, Json);

    /// <summary>Writes a plaintext <c>.maFile</c>.</summary>
    public static void ExportPlain(SteamGuardAccount account, string path)
        => File.WriteAllText(path, Serialize(account));

    /// <summary>
    /// Writes an encrypted <c>.maFile</c> as a self-contained v2 envelope — no companion
    /// manifest needed to re-import it.
    /// </summary>
    public static void ExportEncrypted(SteamGuardAccount account, string path, string password)
        => File.WriteAllText(path, FileEncryptorV2.Encrypt(password, Serialize(account)));
}
