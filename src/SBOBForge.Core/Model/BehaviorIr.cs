using System;
using System.Collections.Generic;
using System.Linq;

namespace SBOBForge.Core.Model;

/// <summary>Event: the workload may spawn this child process (spec 4.5 execs).</summary>
public sealed record ExecEvent(
    string Path,
    IReadOnlyList<string> Args,
    bool Argv0Anchored,
    ResolutionTier Tier,
    IReadOnlyList<Provenance> Provenance);

/// <summary>Event: the workload may open this path with these flags (spec 4.6 opens).</summary>
public sealed record OpenEvent(
    string Path,
    IReadOnlyList<string> Flags,
    ResolutionTier Tier,
    IReadOnlyList<Provenance> Provenance);

/// <summary>Event: permitted network peer (spec 4.7 egress/ingress).</summary>
public sealed record NetEvent(
    string DnsName,
    IReadOnlyList<string> IpAddresses,
    int? Port,
    string Protocol,
    NetDirection Direction,
    ResolutionTier Tier,
    IReadOnlyList<Provenance> Provenance);

/// <summary>A quarantined candidate: seen in tests, not statically resolvable (TDS 7.3 tier Q).</summary>
public sealed record QuarantinedItem(
    BehaviorPrimitive Primitive,
    string Reason,
    string File,
    int Line,
    string TestId,
    string CatalogRuleId);

/// <summary>Test-harness behavior excluded from sections but reported (TDS 7.4).</summary>
public sealed record NoiseItem(string Description, string File, int Line, string TestId);

/// <summary>Primitives the spec v0.0.4 cannot represent (TDS 15): delete, rename, symlink, chmod.</summary>
public sealed record UnrepresentableItem(string Primitive, string File, int Line, string TestId);

/// <summary>
/// Honest-coverage report (TDS 8.4, 15): what was lifted, what was not, and
/// why.
/// </summary>
public sealed record CoverageReport(
    IReadOnlyList<QuarantinedItem> Quarantined,
    IReadOnlyList<NoiseItem> Noise,
    IReadOnlyList<UnrepresentableItem> Unrepresentable,
    int FilesAnalyzed,
    int TestFiles,
    int TestEntries)
{
    public static CoverageReport Empty { get; } = new(
        Array.Empty<QuarantinedItem>(), Array.Empty<NoiseItem>(),
        Array.Empty<UnrepresentableItem>(), 0, 0, 0);
}

/// <summary>
/// Language-neutral behavior IR (TDS 11). Events are stored deduplicated and
/// canonically sorted at construction; the behavior hash is derived from the
/// sorted event sets only (adding provenance to an unchanged entry does not
/// change it).
/// </summary>
public sealed class BehaviorIr
{
    public const int CurrentVersion = 1;

    public int IrVersion { get; }
    public PackageIdentity Package { get; }
    public IReadOnlyList<ExecEvent> Execs { get; }
    public IReadOnlyList<OpenEvent> Opens { get; }
    public IReadOnlyList<NetEvent> Egress { get; }
    public IReadOnlyList<NetEvent> Ingress { get; }
    public CoverageReport Coverage { get; }
    public string GrammarLockHash { get; }
    public string ConfigHash { get; }
    public IReadOnlyList<string> Architectures { get; }

    public BehaviorIr(
        PackageIdentity package,
        IEnumerable<ExecEvent> execs,
        IEnumerable<OpenEvent> opens,
        IEnumerable<NetEvent> egress,
        IEnumerable<NetEvent> ingress,
        CoverageReport coverage,
        string grammarLockHash,
        string configHash,
        IReadOnlyList<string>? architectures = null)
    {
        IrVersion = CurrentVersion;
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Execs = Canonicalize(execs, e => e.Path + "\u0001" + e.Argv0Anchored + "\u0001" + string.Join("\u0001", e.Args));
        Opens = Canonicalize(opens, o => o.Path + "\u0001" + string.Join("\u0001", o.Flags));
        Egress = Canonicalize(egress, n => n.DnsName + "\u0001" + string.Join(",", n.IpAddresses) + "\u0001" + (n.Port ?? -1) + "\u0001" + n.Direction);
        Ingress = Canonicalize(ingress, n => n.DnsName + "\u0001" + string.Join(",", n.IpAddresses) + "\u0001" + (n.Port ?? -1) + "\u0001" + n.Direction);
        Coverage = coverage ?? CoverageReport.Empty;
        GrammarLockHash = grammarLockHash ?? string.Empty;
        ConfigHash = configHash ?? string.Empty;
        Architectures = architectures ?? (package.TargetOs == "linux" ? new[] { "amd64" } : Array.Empty<string>());
    }

    /// <summary>Deduplicate by key (ordinal sort), merge provenance of identical entries. Ordinal comparison only (TDS 9).</summary>
    private static IReadOnlyList<T> Canonicalize<T>(IEnumerable<T>? events, Func<T, string> key)
        where T : notnull
    {
        if (events is null) return Array.Empty<T>();
        var groups = new SortedDictionary<string, List<Provenance>>(StringComparer.Ordinal);
        var firstByKey = new Dictionary<string, T>();
        foreach (var e in events)
        {
            var k = key(e);
            var prov = e switch
            {
                ExecEvent x => x.Provenance,
                OpenEvent x => x.Provenance,
                NetEvent x => x.Provenance,
                _ => Array.Empty<Provenance>()
            };
            if (!groups.TryGetValue(k, out var list))
            {
                groups[k] = list = new List<Provenance>();
                firstByKey[k] = e;
            }
            list.AddRange(prov);
        }
        var result = new List<T>(groups.Count);
        foreach (var (k, prov) in groups)
        {
            var first = firstByKey[k];
            var merged = prov
                .Distinct()
                .OrderBy(p => p.File, StringComparer.Ordinal)
                .ThenBy(p => p.Line)
                .ThenBy(p => p.TestId, StringComparer.Ordinal)
                .ToArray();
            result.Add(first switch
            {
                ExecEvent x => (T)(object)(x with { Provenance = merged }),
                OpenEvent x => (T)(object)(x with { Provenance = merged }),
                NetEvent x => (T)(object)(x with { Provenance = merged }),
                _ => first
            });
        }
        return result;
    }

    /// <summary>sha256 over the normalized IR (TDS 8.3): content-derived, clock-free.</summary>
    public string ComputeBehaviorHash()
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(NormalizedForm());
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    /// <summary>Canonical string form of the event sets (provenance excluded).</summary>
    public string NormalizedForm()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("sbobIrVersion=").Append(IrVersion).Append('\n');
        sb.Append("package=").Append(Package.ImageId).Append('\n');
        foreach (var e in Execs)
            sb.Append("exec\t").Append(e.Path).Append('\t').Append(e.Argv0Anchored)
              .Append('\t').Append(string.Join("\u0001", e.Args)).Append('\n');
        foreach (var o in Opens)
            sb.Append("open\t").Append(o.Path).Append('\t').Append(string.Join(",", o.Flags)).Append('\n');
        foreach (var n in Egress)
            sb.Append("egress\t").Append(n.DnsName).Append('\t').Append(string.Join(",", n.IpAddresses))
              .Append('\t').Append(n.Port?.ToString() ?? "-").Append('\t').Append(n.Protocol).Append('\n');
        foreach (var n in Ingress)
            sb.Append("ingress\t").Append(n.DnsName).Append('\t').Append(string.Join(",", n.IpAddresses))
              .Append('\t').Append(n.Port?.ToString() ?? "-").Append('\t').Append(n.Protocol).Append('\n');
        sb.Append("lock=").Append(GrammarLockHash).Append('\n');
        sb.Append("config=").Append(ConfigHash).Append('\n');
        return sb.ToString();
    }
}
