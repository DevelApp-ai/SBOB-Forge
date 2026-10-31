using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));

    /// <summary>Content-derived lock identity (annotation develapp.io/grammar-lock-sha256, TDS 8.2).</summary>
    public string ComputeLockHash()
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions(Json.Options)
        {
            WriteIndented = false
        });
        return Convert.ToHexString(sha.ComputeHash(canonical)).ToLowerInvariant();
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
