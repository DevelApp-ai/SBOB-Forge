using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SBOBForge.Core.Emit;

namespace SBOBForge.Core.Config;

/// <summary>
/// sbobforge.lock.json — pinned parsing substrate (TDS 5.3). Grammar entries
/// map to marketplace artifacts once grammar distribution is consumable; in
/// IR v1 the Minotaur engine package itself is the pinned substrate.
/// </summary>
public sealed class GrammarLock
{
    [JsonPropertyName("sbobIrVersion")]
    public int SbobIrVersion { get; set; } = Model.BehaviorIr.CurrentVersion;

    public Dictionary<string, GrammarLockEntry> Grammars { get; set; } = new(StringComparer.Ordinal);

    public MinotaurLockEntry Minotaur { get; set; } = new();

    public static GrammarLock Load(string path)
    {
        var json = File.ReadAllText(path);
        var lockFile = JsonSerializer.Deserialize<GrammarLock>(json, Json.Options)
            ?? throw new InvalidDataException($"Empty or invalid lock file: {path}");
        if (lockFile.SbobIrVersion != Model.BehaviorIr.CurrentVersion)
            throw new InvalidDataException(
                $"Lock file IR version {lockFile.SbobIrVersion} != tool IR version {Model.BehaviorIr.CurrentVersion}");
        return lockFile;
    }

    public void Save(string path)
    {
        CanonicalizeKeyOrder();
        // TDS 9 (platform byte-identity): the indented string serializer's
        // line separator is Environment.NewLine — normalize so the committed
        // lock is LF on every platform (org decision #26).
        File.WriteAllText(path, CanonicalText.LfOnly(JsonSerializer.Serialize(this, Json.Options)));
    }

    /// <summary>Content-derived lock identity (annotation develapp.io/grammar-lock-sha256, TDS 8.2).</summary>
    public string ComputeLockHash()
    {
        CanonicalizeKeyOrder();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions(Json.Options)
        {
            WriteIndented = false
        });
        return Convert.ToHexString(sha.ComputeHash(canonical)).ToLowerInvariant();
    }

    /// <summary>
    /// Grammar entries serialize in ordinal key order so identical pin sets are
    /// byte-identical and hash-identical regardless of insertion order (TDS 5.3).
    /// </summary>
    private void CanonicalizeKeyOrder()
    {
        if (Grammars.Count < 2) return;
        var sorted = new Dictionary<string, GrammarLockEntry>(StringComparer.Ordinal);
        foreach (var kv in Grammars.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sorted[kv.Key] = kv.Value;
        Grammars = sorted;
    }

    /// <summary>Frozen-mode verification (TDS 5.3): the installed Minotaur version must equal the pinned one.</summary>
    public void VerifyFrozen(string installedMinotaurVersion)
    {
        var pinned = Minotaur.Version;
        if (!string.Equals(pinned, installedMinotaurVersion, StringComparison.Ordinal))
            throw new LockMismatchException(
                $"Grammar lock mismatch: sbobforge.lock.json pins DevelApp.Minotaur {pinned}, " +
                $"installed is {installedMinotaurVersion}. Run 'sbobforge lock --update' and review like a dependency bump.");
    }

    /// <summary>
    /// Frozen-mode verification for a pinned grammar entry (TDS 5.3): the installed
    /// backend/catalog version must equal the pinned one, and a pinned sha256 (catalogs)
    /// must match the installed artifact hash.
    /// </summary>
    public void VerifyFrozenEntry(string entry, string installedVersion, string? installedSha256)
    {
        if (!Grammars.TryGetValue(entry, out var pinned))
            throw new LockMismatchException(
                $"Grammar lock mismatch: '{entry}' is not pinned in sbobforge.lock.json. " +
                "Run 'sbobforge lock --update' and review like a dependency bump.");
        if (!string.Equals(pinned.Version, installedVersion, StringComparison.Ordinal))
            throw new LockMismatchException(
                $"Grammar lock mismatch: sbobforge.lock.json pins {entry} {pinned.Version}, " +
                $"installed is {installedVersion}. Run 'sbobforge lock --update' and review like a dependency bump.");
        if (installedSha256 is not null && !string.Equals(pinned.Sha256, installedSha256, StringComparison.Ordinal))
            throw new LockMismatchException(
                $"Grammar lock mismatch: sbobforge.lock.json pins {entry} sha256 {pinned.Sha256}, " +
                $"installed artifact hash is {installedSha256}. Run 'sbobforge lock --update' and review like a dependency bump.");
    }
}

public sealed class GrammarLockEntry
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class MinotaurLockEntry
{
    public string Version { get; set; } = "";
}

/// <summary>Exit code 5 (TDS 13): lock mismatch in --frozen mode.</summary>
public sealed class LockMismatchException(string message) : Exception(message);
