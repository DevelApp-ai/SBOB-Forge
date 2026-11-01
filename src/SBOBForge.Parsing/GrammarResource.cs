using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace SBOBForge.Parsing;

/// <summary>
/// Embedded grammar artifacts (TDS 5.4 Phase B): the authored PowerShell 7.4
/// and Bash 5 grammars committed to Minotaur-Grammars (PR #196) are embedded
/// in this assembly byte-identical. The grammar-lock pins their sha256, and
/// `--frozen` verifies it against the embedded copy — no network fetch, the
/// same self-contained posture as the behavior catalogs.
/// </summary>
public static class GrammarResource
{
    public const string PowerShellKey = "powershell";
    public const string BashKey = "bash";

    private static readonly Dictionary<string, string> s_cache = new(StringComparer.Ordinal);
    private static readonly object s_gate = new();

    /// <summary>
    /// sha256 (lowercase hex) of the embedded grammar artifact for a lock
    /// grammar key ("powershell"/"bash"). Throws InvalidDataException when the
    /// key has no embedded artifact (contract violation, never data-dependent).
    /// </summary>
    public static string Sha256(string grammarKey)
    {
        lock (s_gate)
        {
            if (s_cache.TryGetValue(grammarKey, out var cached))
                return cached;
            var assembly = typeof(GrammarResource).Assembly;
            // lock keys are lowercase ("powershell"); the embedded file names carry
            // natural casing ("PowerShell.grammar") — match the manifest tail
            // case-insensitively (ordinal, no locale).
            var suffix = $".Grammars.{grammarKey}.grammar";
            string? found = null;
            foreach (var name in assembly.GetManifestResourceNames())
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    found = name;
                    break;
                }
            if (found is null)
                throw new InvalidDataException(
                    $"grammar artifact '{grammarKey}' is not embedded in {assembly.GetName().Name}");
            using var stream = assembly.GetManifestResourceStream(found)
                ?? throw new InvalidDataException($"embedded grammar '{found}' cannot be opened");
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
            s_cache[grammarKey] = hash;
            return hash;
        }
    }
}
