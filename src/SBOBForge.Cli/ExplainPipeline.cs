using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SBOBForge.Core.Config;
using SBOBForge.Core.Emit;
using SBOBForge.Core.Model;

namespace SBOBForge.Cli;

public sealed class ExplainOptions
{
    public required string RepoRoot { get; init; }
    public required string Query { get; init; }
    public string? ConfigPath { get; init; }
    public bool JsonOutput { get; init; }
}

/// <summary>One provenance sidecar record (sbob/&lt;package&gt;.provenance.jsonl, TDS 8.3).</summary>
public sealed record ProvenanceRecord(
    string Primitive, string Identifier, string TestId, string File, int Line, string Tier, string Rule);

/// <summary>
/// M7 `sbobforge explain` (TDS 8.3, SBOB-Forge #6): provenance sidecar lookup —
/// entry → testIds, file:line, tier (resolution tier), catalog rule id, and the
/// pinned grammar/parser + catalog versions that produced the evidence
/// ("opened in ConfigStore.cs, exercised by 2 xUnit facts; Tier L").
/// Read-only: explain never regenerates or mutates artifacts.
/// </summary>
public sealed record ExplainResult(
    IReadOnlyList<ExplainRow> Rows,
    bool ExactMatch,
    string ProvenancePath)
{
    /// <summary>True when the query produced no rows (exit code 2 at the CLI surface).</summary>
    public bool NotFound => Rows.Count == 0;
}

public sealed record ExplainRow(
    string Primitive,
    string Identifier,
    string TestId,
    string File,
    int Line,
    string Tier,
    string CatalogRule,
    string Grammar);

public static class ExplainPipeline
{
    public static ExplainResult Run(ExplainOptions options)
    {
        var root = Path.GetFullPath(options.RepoRoot);
        var configPath = Path.GetFullPath(options.ConfigPath ?? Path.Combine(root, "sbobforge.json"), root);
        var config = SbobForgeConfig.Load(configPath);
        var package = new Core.Model.PackageIdentity(
            config.PackageType, config.PackageName, config.PackageVersion, config.TargetOs);
        var provenancePath = Path.Combine(root, "sbob", package.MetadataName + ".provenance.jsonl");
        if (!File.Exists(provenancePath))
            throw new InvalidDataException(
                $"provenance sidecar not found: {provenancePath} — run 'sbobforge generate {root}' first");

        var records = new List<ProvenanceRecord>();
        foreach (var line in File.ReadAllLines(provenancePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            records.Add(JsonSerializer.Deserialize<ProvenanceRecord>(line, Json.Options)
                ?? throw new InvalidDataException($"invalid provenance row in {provenancePath}: {line}"));
        }

        // Lookup is exact-first (ordinal); a case-insensitive substring fallback keeps the
        // CLI ergonomic for partial identifiers. Everything below is ordinal-sorted —
        // explain output is deterministic for pinned sidecar bytes (TDS 9).
        var exact = records
            .Where(r => string.Equals(r.Identifier, options.Query, StringComparison.Ordinal)).ToList();
        var exactMatch = exact.Count > 0;
        var matched = exactMatch
            ? exact
            : records.Where(r => r.Identifier.Contains(options.Query, StringComparison.OrdinalIgnoreCase)).ToList();
        matched = matched
            .OrderBy(r => r.Primitive, StringComparer.Ordinal)
            .ThenBy(r => r.Identifier, StringComparer.Ordinal)
            .ThenBy(r => r.File, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.TestId, StringComparer.Ordinal)
            .ToList();

        // Grammar resolution (TDS 8.3 "grammar version"): the language of each matched
        // row's file selects the pinned parser + catalog lock entries. An absent lock
        // is reported as unpinned rather than failing the lookup — the sidecar rows
        // themselves are the answer.
        GrammarLock? lockFile = null;
        var lockPath = Path.Combine(root, "sbobforge.lock.json");
        if (File.Exists(lockPath)) lockFile = GrammarLock.Load(lockPath);
        var grammarCache = new Dictionary<(string ParserKey, string CatalogKey), string>(new TupleComparer());
        var rows = new List<ExplainRow>(matched.Count);
        foreach (var r in matched)
        {
            var lang = GeneratePipeline.LangOf(r.File);
            var parserKey = GeneratePipeline.ParserLockKey(lang);
            var catalogKey = GeneratePipeline.CatalogLockKey(lang);
            if (!grammarCache.TryGetValue((parserKey, catalogKey), out var grammar))
            {
                var parserVersion = "<unpinned>";
                if (lockFile is not null && lockFile.Grammars.TryGetValue(parserKey, out var p))
                    parserVersion = p.Version + (p.Sha256.Length > 0 ? " sha256 " + p.Sha256[..12] : "");
                var catalogVersion = "<unpinned>";
                if (lockFile is not null && lockFile.Grammars.TryGetValue(catalogKey, out var c))
                    catalogVersion = "v" + c.Version;
                grammar = $"{parserKey} {parserVersion}, {catalogKey} {catalogVersion}";
                grammarCache[(parserKey, catalogKey)] = grammar;
            }
            rows.Add(new ExplainRow(
                r.Primitive, r.Identifier, r.TestId, r.File, r.Line, r.Tier, r.Rule, grammar));
        }

        return new ExplainResult(rows, exactMatch, provenancePath);
    }

    /// <summary>Deterministic human-readable rendering (TDS 8.3 example shape).</summary>
    public static string Render(ExplainResult result)
    {
        if (result.NotFound) return "";
        var sb = new StringBuilder();
        foreach (var group in result.Rows
                     .GroupBy(r => (r.Primitive, r.Identifier))
                     .OrderBy(g => g.Key.Primitive, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Identifier, StringComparer.Ordinal))
        {
            var rows = group.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line)
                .ThenBy(r => r.TestId, StringComparer.Ordinal).ToList();
            var facts = rows.Count(r => !r.TestId.StartsWith("sut:", StringComparison.Ordinal));
            sb.Append(group.Key.Primitive).Append(' ').Append(group.Key.Identifier).Append(" — ")
              .Append(facts).Append(" test fact(s), ")
              .Append(rows.Count - facts).Append(" SUT site(s)\n");
            foreach (var r in rows)
                sb.Append("  ").Append(r.Tier)
                  .Append("  ").Append(r.CatalogRule)
                  .Append("  ").Append(r.File).Append(':').Append(r.Line)
                  .Append("  ").Append(r.TestId).Append('\n');
            var grammars = rows.Select(r => r.Grammar).Distinct().OrderBy(g => g, StringComparer.Ordinal);
            foreach (var g in grammars)
                sb.Append("  grammar: ").Append(g).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Machine-readable rendering (--json; deterministic order, LF-only).</summary>
    public static string RenderJson(ExplainResult result)
    {
        var doc = CanonicalText.LfOnly(JsonSerializer.Serialize(result.Rows, Json.Options));
        return doc + "\n";
    }

    private sealed class TupleComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) => x.Item1 == y.Item1 && x.Item2 == y.Item2;
        public int GetHashCode((string, string) obj) => HashCode.Combine(obj.Item1, obj.Item2);
    }
}
