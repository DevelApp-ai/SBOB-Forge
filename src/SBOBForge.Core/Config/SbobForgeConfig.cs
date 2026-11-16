using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SBOBForge.Core.Config;

/// <summary>
/// sbobforge.json — per-package generator configuration (TDS 5.1). All file
/// globs are repo-relative and use '/' separators; discovery output is ordinal-
/// sorted, so this config is fully deterministic.
/// </summary>
public sealed class SbobForgeConfig
{
    public string PackageType { get; set; } = "nuget";
    public string PackageName { get; set; } = "";
    public string PackageVersion { get; set; } = "";
    public string Namespace { get; set; } = "develapp";
    public string TargetOs { get; set; } = "linux";
    public List<string> Architectures { get; set; } = new() { "amd64" };

    /// <summary>Globs matching SUT (shipped) source files.</summary>
    public List<string> SutGlobs { get; set; } = new();

    /// <summary>Globs matching test source files.</summary>
    public List<string> TestGlobs { get; set; } = new();

    /// <summary>Declared runtime placeholders: literal or prefix match, emitted wildcard pattern (TDS 7.3 tier P).</summary>
    public List<PathPolicy> PathPolicies { get; set; } = new();

    /// <summary>
    /// Declared dynamic-egress placeholders (SBOB-Forge #49, TDS 7.3 tier P): a
    /// Connect candidate whose URL/host lowering quarantines as an identifier is
    /// lifted to a tier-P egress entry when a policy's <see cref="NetPolicy.Match"/>
    /// equals that identifier (variable-identity matching, Lars 2026-11-16).
    /// </summary>
    public List<NetPolicy> NetPolicies { get; set; } = new();

    /// <summary>Bare exec names to declared runtime locations (never resolved against the host PATH; TDS 9).</summary>
    public Dictionary<string, string> ExecPathPolicy { get; set; } = new();

    /// <summary>Files allowed to fail parsing (vendored fixtures); recorded in the coverage report.</summary>
    public List<string> AllowParseError { get; set; } = new();

    public string ImageId => $"pkg:{PackageType}/{PackageName.ToLowerInvariant()}@{PackageVersion}";

    public static SbobForgeConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<SbobForgeConfig>(json, Json.Options)
            ?? throw new InvalidDataException($"Empty or invalid config: {path}");
        if (string.IsNullOrWhiteSpace(cfg.PackageName) || string.IsNullOrWhiteSpace(cfg.PackageVersion))
            throw new InvalidDataException($"Config must set packageName and packageVersion: {path}");
        ValidateNetPolicies(cfg.NetPolicies, path);
        return cfg;
    }

    /// <summary>
    /// netPolicies are the fail-closed declaration surface for dynamic egress
    /// (SBOB-Forge #49): every entry needs a non-blank id (the coverage-row rule),
    /// match (the identifier at the quarantine site) and emit (a DNS-name pattern:
    /// the bare segment wildcard '⋯' or a constrained pattern like '*.internal' —
    /// Lars 2026-11-16). Ids must be unique; emits must be DNS names, not URLs.
    /// </summary>
    private static void ValidateNetPolicies(List<NetPolicy> policies, string path)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in policies)
        {
            if (string.IsNullOrWhiteSpace(p.Id))
                throw new InvalidDataException($"netPolicies entry (match '{p.Match}') must declare a non-blank id — it is the coverage-row rule: {path}");
            if (string.IsNullOrWhiteSpace(p.Match))
                throw new InvalidDataException($"netPolicies entry '{p.Id}' must declare a non-blank match (the identifier at the quarantine site): {path}");
            if (string.IsNullOrWhiteSpace(p.Emit))
                throw new InvalidDataException($"netPolicies entry '{p.Id}' must declare a non-blank emit (a DNS-name pattern, e.g. '⋯' or '*.internal'): {path}");
            if (!seen.Add(p.Id))
                throw new InvalidDataException($"netPolicies id '{p.Id}' is declared more than once: {path}");
            if (p.Emit.Contains('/') || p.Emit.Contains(':') || p.Emit.Any(char.IsWhiteSpace))
                throw new InvalidDataException($"netPolicies entry '{p.Id}' emit '{p.Emit}' is not a DNS-name pattern (no scheme, path or whitespace): {path}");
            if (!IsSpec58DnsPattern(p.Emit))
                throw new InvalidDataException(
                    $"netPolicies entry '{p.Id}' emit '{p.Emit}' is not a spec 5.8 DNS pattern (allowed: a plain name, " +
                    "'*.suffix', 'prefix.*', 'a.⋯.b.'; bare '⋯' or '*' = the any-peer declaration): " + path);
        }
    }

    /// <summary>
    /// Spec 5.8 DNS wildcard shapes, mirroring the downstream contract (sbobctl
    /// compileDnsPattern): plain name, leading '*.suffix', trailing 'prefix.*',
    /// or '⋯' between two labels ('a.⋯.b.'). The bare '⋯'/'*' any-peer
    /// declaration is accepted separately (it never lands in dnsNames).
    /// </summary>
    private static bool IsSpec58DnsPattern(string emit)
    {
        if (emit == "⋯" || emit == "*") return true; // the any-peer declaration
        if (!emit.Contains('*') && !emit.Contains("⋯")) return true; // plain name
        if (emit.StartsWith("*.") && emit.Length > 2)
            return !emit[2..].Contains('*') && !emit[2..].Contains("⋯");
        if (emit.EndsWith(".*") && emit.Length > 2)
            return !emit[..^2].Contains('*') && !emit[..^2].Contains("⋯");
        if (emit.Contains("⋯"))
        {
            var parts = emit.Split("⋯");
            return parts.Length == 2
                && parts[0].Length > 0 && parts[1].Length > 0
                && parts[0].EndsWith('.') && parts[1].StartsWith('.')
                && !parts[0].Contains('*') && !parts[1].Contains('*');
        }
        return false; // mid-'*' or multi-token shapes are not representable downstream
    }

    /// <summary>Content hash of the config file: part of behavior-hash input (TDS 8.3).</summary>
    public static string ComputeHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}

/// <summary>One declared placeholder mapping: e.g. %PackageRoot% → /app/⋯ (TDS 7.3).</summary>
public sealed class PathPolicy
{
    /// <summary>Literal string or string-prefix that identifies the runtime placeholder in code.</summary>
    public string Match { get; set; } = "";

    /// <summary>Pattern emitted into the SBOB when the tier resolves to Pattern.</summary>
    public string Emit { get; set; } = "";

    /// <summary>When true, a path starting with <see cref="Match"/> maps to <see cref="Emit"/> plus '/*'.</summary>
    public bool Prefix { get; set; }

    /// <summary>
    /// Policy identity. Required only when the entry lifts identifier-valued open
    /// paths (SBOB-Forge #49): the lifted candidate's coverage row carries this id
    /// as its rule. Legacy literal/prefix entries keep the catalog rule id.
    /// </summary>
    public string Id { get; set; } = "";
}

/// <summary>
/// One declared dynamic-egress placeholder (SBOB-Forge #49): a Connect candidate
/// whose URL/host quarantines as the identifier <see cref="Match"/> lifts to a
/// tier-P egress entry with <see cref="Emit"/> as the DNS-name pattern.
/// </summary>
public sealed class NetPolicy
{
    /// <summary>Coverage-row rule for lifted candidates (Lars 2026-11-16: tier P with the policy id as CatalogRuleId).</summary>
    public string Id { get; set; } = "";

    /// <summary>The identifier at the quarantine site (variable-identity matching).</summary>
    public string Match { get; set; } = "";

    /// <summary>
    /// The emitted destination pattern. The bare form '⋯' (or '*') is the declared
    /// any-peer placeholder and materializes as the spec 5.7 any-peer entry
    /// (ipAddresses: ["*"]) — spec 5.8 admits '⋯' in DNS patterns only between two
    /// labels. Constrained patterns must be spec 5.8 DNS shapes: '*.suffix',
    /// 'prefix.*', 'a.⋯.b.', or a plain name.
    /// </summary>
    public string Emit { get; set; } = "";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };
}
