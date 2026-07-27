using System.Text.Json;
using Vouch.Core.Steam;

namespace Vouch.Core.Storage;

/// <summary>
/// Manages a <c>maFiles/</c> directory of accounts plus an <c>entries.json</c> index that also holds the
/// sidebar layout (account order + groups; see <see cref="MaFileIndex"/>). Whether the directory is
/// encrypted at rest lives in app settings; when encrypted, every maFile is a self-contained v2 envelope
/// (Argon2id + AES-256-GCM) under a single session passkey held in memory after <see cref="TryUnlock"/> /
/// <see cref="EnableEncryption"/>.
/// </summary>
public class MaFileRepository
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string? _passkey;
    private readonly string _settingsPath;

    public string MaFilesDir { get; }

    public MaFileRepository(string maFilesDir, string? settingsPath = null)
    {
        MaFilesDir = maFilesDir;
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
        Directory.CreateDirectory(maFilesDir);
        ReconcileEncryptionState();
    }

    /// <summary>Self-heals the encryption flag against what's actually on disk. The flag lives in
    /// app settings, separate from the maFiles; if settings.json is lost or reset while the directory
    /// holds encrypted maFiles, the flag would read <c>false</c> and the app would try to parse
    /// ciphertext as plaintext JSON — silently failing with no chance to enter the passkey. So when the
    /// flag says "plaintext" but a real v2 envelope is found on disk, flip it back on. Only ever corrects
    /// <c>false → true</c>, and only with hard evidence, so it can never wrongly hide plaintext accounts.</summary>
    private void ReconcileEncryptionState()
    {
        if (IsEncrypted) return; // already flagged encrypted — nothing to correct

        foreach (var file in Directory.EnumerateFiles(MaFilesDir, "*.maFile"))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            if (FileEncryptorV2.LooksLikeV2(text))
            {
                SetEncrypted(true);
                return;
            }
        }
    }

    private string EntriesPath => Path.Combine(MaFilesDir, "entries.json");

    /// <summary>Whether the directory is encrypted at rest. Stored in app settings, but reconciled
    /// against the actual files at construction (see <see cref="ReconcileEncryptionState"/>) so a lost
    /// or reset settings.json can't leave encrypted maFiles undetected.</summary>
    public bool IsEncrypted => AppSettings.LoadFrom(_settingsPath).Encrypted;

    private void SetEncrypted(bool value)
    {
        var s = AppSettings.LoadFrom(_settingsPath);
        s.Encrypted = value;
        s.SaveTo(_settingsPath);
    }

    /// <summary>True when the directory is encrypted and no valid passkey has been given yet.</summary>
    public bool RequiresPasskey => IsEncrypted && _passkey is null;

    /// <summary>Verifies the passkey by decrypting every entry; kept for the session on success.</summary>
    public bool TryUnlock(string passkey)
    {
        if (string.IsNullOrEmpty(passkey)) return false;
        if (!IsEncrypted) { _passkey = null; return true; }

        var accounts = ReadIndex().Accounts;
        if (accounts.Count == 0) return false; // nothing to verify against
        foreach (var e in accounts)
        {
            if (e.Filename is null) continue;
            var path = Path.Combine(MaFilesDir, e.Filename);
            if (!File.Exists(path)) continue;
            if (MaFileStore.LoadFile(path, passkey).Status is MaFileLoadStatus.WrongPassword or MaFileLoadStatus.NeedsPassword)
                return false;
        }
        _passkey = passkey;
        return true;
    }

    /// <summary>Loads every account from the <c>entries.json</c> index (both modes). Encrypted
    /// directories must be unlocked first (returns empty otherwise). Plaintext directories are first
    /// reconciled — any <c>*.maFile</c> copied in without an index entry is adopted — so dropping a
    /// file into the folder still works.</summary>
    public IReadOnlyList<SteamGuardAccount> LoadAll()
    {
        if (RequiresPasskey) return Array.Empty<SteamGuardAccount>();

        if (!IsEncrypted) ReconcilePlaintextEntries();

        var list = new List<SteamGuardAccount>();
        foreach (var e in ReadIndex().Accounts)
        {
            if (e.Filename is null) continue;
            var path = Path.Combine(MaFilesDir, e.Filename);
            if (!File.Exists(path)) continue;
            if (MaFileStore.LoadFile(path, _passkey) is { Status: MaFileLoadStatus.Ok, Account: { } acc })
                list.Add(acc);
        }
        return list;
    }

    /// <summary>Keeps a plaintext directory's index in sync with what's on disk: adopts any
    /// <c>*.maFile</c> not yet indexed (files copied in by hand) and drops entries whose file is gone.
    /// Encrypted directories can't be reconciled — their contents are opaque without the passkey.</summary>
    private void ReconcilePlaintextEntries()
    {
        var index = ReadIndex();
        var indexed = new HashSet<string>(
            index.Accounts.Where(e => e.Filename is not null).Select(e => e.Filename!),
            StringComparer.OrdinalIgnoreCase);
        var changed = false;

        // Drop entries pointing at files that no longer exist.
        changed |= index.Accounts.RemoveAll(e => e.Filename is null || !File.Exists(Path.Combine(MaFilesDir, e.Filename))) > 0;

        // Adopt any plaintext maFile that isn't indexed yet.
        foreach (var file in Directory.EnumerateFiles(MaFilesDir, "*.maFile"))
        {
            var name = Path.GetFileName(file);
            if (indexed.Contains(name)) continue;
            if (MaFileStore.LoadFile(file) is { Status: MaFileLoadStatus.Ok, Account: { } acc })
            {
                var steamId = acc.Session?.SteamId ?? 0;
                index.Accounts.RemoveAll(e => e.SteamId == steamId);
                index.Accounts.Add(new MaFileEntry { Filename = name, SteamId = steamId });
                changed = true;
            }
        }

        if (changed) WriteIndex(index);
    }

    /// <summary>Writes/updates a <c>{steamid}.maFile</c> (encrypted when the directory is) and its index
    /// entry, preserving the account's existing position and group.</summary>
    public void Save(SteamGuardAccount account)
    {
        if (IsEncrypted && _passkey is null)
            throw new InvalidOperationException("The maFiles directory is encrypted and locked — unlock it before saving.");

        var steamId = account.Session?.SteamId ?? 0;
        var filename = $"{steamId}.maFile";
        var path = Path.Combine(MaFilesDir, filename);

        if (IsEncrypted)
            File.WriteAllText(path, FileEncryptorV2.Encrypt(_passkey!, MaFileStore.Serialize(account)));
        else
            MaFileStore.ExportPlain(account, path);

        var index = ReadIndex();
        var existing = index.Accounts.FirstOrDefault(e => e.SteamId == steamId);
        if (existing is not null)
            existing.Filename = filename; // keep its position and group
        else
            index.Accounts.Add(new MaFileEntry { Filename = filename, SteamId = steamId });
        WriteIndex(index);
    }

    /// <summary>Persists the sidebar layout: the account order (order of <paramref name="orderedAccounts"/>)
    /// and each account's group, plus the group order and collapsed state. Filenames are carried over from
    /// the current index (a missing one falls back to <c>{steamid}.maFile</c>).</summary>
    public void SaveLayout(IReadOnlyList<MaFileEntry> orderedAccounts, IReadOnlyList<GroupEntry> groups)
    {
        var current = ReadIndex();
        var filenames = current.Accounts
            .Where(e => e.Filename is not null)
            .GroupBy(e => e.SteamId)
            .ToDictionary(g => g.Key, g => g.Last().Filename!);

        var index = new MaFileIndex
        {
            Accounts = orderedAccounts.Select(a => new MaFileEntry
            {
                Filename = filenames.TryGetValue(a.SteamId, out var f) ? f : $"{a.SteamId}.maFile",
                SteamId = a.SteamId,
                Group = string.IsNullOrEmpty(a.Group) ? null : a.Group,
            }).ToList(),
            Groups = groups.Where(g => !string.IsNullOrEmpty(g.Name)).ToList(),
        };
        WriteIndex(index);
    }

    /// <summary>Reads the current index (account order + groups). Works even while locked — the index
    /// itself is never encrypted.</summary>
    public MaFileIndex GetIndex() => ReadIndex();

    /// <summary>Deletes an account's file and index entry. Irreversible — caller must confirm.</summary>
    public void Delete(ulong steamId)
    {
        var path = Path.Combine(MaFilesDir, $"{steamId}.maFile");
        if (File.Exists(path)) File.Delete(path);

        var index = ReadIndex();
        index.Accounts.RemoveAll(e => e.SteamId == steamId);
        WriteIndex(index);
    }

    /// <summary>In an encrypted, unlocked directory, lists any <c>*.maFile</c> that is still plaintext
    /// (e.g. dropped into the folder by hand, or left behind by an interrupted encryption). These hold a
    /// full account in the clear despite the vault being "encrypted", so the app offers to fix them.
    /// Empty when the directory isn't encrypted, is still locked, or every file is already a v2 envelope.</summary>
    public IReadOnlyList<string> FindPlaintextMaFiles()
    {
        if (!IsEncrypted || _passkey is null) return Array.Empty<string>();

        var loose = new List<string>();
        foreach (var file in Directory.EnumerateFiles(MaFilesDir, "*.maFile"))
        {
            // LoadFile with no password: plaintext → Ok; a v2 envelope → NeedsPassword; junk → Invalid.
            if (MaFileStore.LoadFile(file).Status == MaFileLoadStatus.Ok)
                loose.Add(Path.GetFileName(file));
        }
        return loose;
    }

    /// <summary>Re-encrypts any plaintext maFiles in an encrypted, unlocked directory (see
    /// <see cref="FindPlaintextMaFiles"/>). Each is rewritten as a v2 envelope under the canonical
    /// <c>{steamid}.maFile</c> name; a differently-named source file is removed after conversion so no
    /// plaintext copy lingers. Returns how many were converted.</summary>
    public int EncryptLooseFiles()
    {
        if (!IsEncrypted || _passkey is null) return 0;

        var n = 0;
        foreach (var file in Directory.EnumerateFiles(MaFilesDir, "*.maFile").ToList())
        {
            if (MaFileStore.LoadFile(file) is not { Status: MaFileLoadStatus.Ok, Account: { } acc }) continue;

            Save(acc); // rewrites {steamid}.maFile as a v2 envelope + updates the index entry
            var canonical = Path.Combine(MaFilesDir, $"{acc.Session?.SteamId ?? 0}.maFile");
            if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(canonical), StringComparison.OrdinalIgnoreCase))
                try { File.Delete(file); } catch { /* best-effort: the canonical encrypted copy already exists */ }
            n++;
        }
        return n;
    }

    /// <summary>Encrypts the whole directory with <paramref name="passkey"/> and keeps it for the session.</summary>
    public void EnableEncryption(string passkey)
    {
        if (string.IsNullOrEmpty(passkey)) throw new ArgumentException("Passkey is empty.", nameof(passkey));
        if (IsEncrypted) return;

        var accounts = LoadAll(); // still plaintext at this point
        _passkey = passkey;
        SetEncrypted(true);
        foreach (var acc in accounts) Save(acc);
    }

    /// <summary>Decrypts the whole directory back to plaintext. Requires an unlocked repository.</summary>
    public void DisableEncryption()
    {
        if (!IsEncrypted) return;
        if (RequiresPasskey)
            throw new InvalidOperationException("Unlock the directory before disabling encryption.");

        var accounts = LoadAll(); // decrypted with the session passkey
        SetEncrypted(false);
        _passkey = null;
        foreach (var acc in accounts) Save(acc); // rewrites plaintext
    }

    /// <summary>Reads <c>entries.json</c> (account order + groups).</summary>
    private MaFileIndex ReadIndex()
    {
        try
        {
            return File.Exists(EntriesPath)
                ? JsonSerializer.Deserialize<MaFileIndex>(File.ReadAllText(EntriesPath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private void WriteIndex(MaFileIndex index) =>
        File.WriteAllText(EntriesPath, JsonSerializer.Serialize(index, Json));
}
