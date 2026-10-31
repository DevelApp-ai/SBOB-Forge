using System;
using System.Collections.Generic;

namespace SBOBForge.Core.Model;

/// <summary>Value-resolution tier (TDS 7.3): Literal, Pattern (declared placeholder), Quarantine.</summary>
public enum ResolutionTier
{
    /// <summary>Argument constant-folds to an absolute literal inside SUT code.</summary>
    Literal,

    /// <summary>Value maps to a declared runtime placeholder via pathPolicies.</summary>
    Pattern,

    /// <summary>Not statically resolvable: no SBOB entry, coverage-report row only.</summary>
    Quarantine
}

/// <summary>Behavior event kinds liftable in IR v1.</summary>
public enum BehaviorPrimitive
{
    Exec,
    Open,
    Connect,
    Listen
}

/// <summary>Network direction for connect/listen events.</summary>
public enum NetDirection
{
    Egress,
    Ingress
}

/// <summary>Provenance record for one lifted event (TDS 8.3).</summary>
public sealed record Provenance(
    string TestId,
    string File,
    int Line,
    ResolutionTier Tier,
    string CatalogRuleId)
{
    public override string ToString() => $"{File}:{Line} ({TestId}, {Tier}, rule {CatalogRuleId})";
}

/// <summary>Canonical identity of the profiled package: purl in spec.imageID (TDS 4).</summary>
public sealed record PackageIdentity(
    string PurlType,
    string Name,
    string Version,
    string TargetOs)
{
    /// <summary>Normalized, lowercase purl, e.g. pkg:nuget/develapp.minotaur@1.32.0</summary>
    public string ImageId => $"pkg:{PurlType}/{Name.ToLowerInvariant()}@{Version}";

    /// <summary>metadata.name: purl name with '.' and '/' replaced by '-'; target-os suffix keeps documents unique.</summary>
    public string MetadataName => Name.ToLowerInvariant().Replace('.', '-').Replace('/', '-') + "." + TargetOs.ToLowerInvariant();

    public static PackageIdentity FromPurl(string purl, string targetOs)
    {
        if (!purl.StartsWith("pkg:", StringComparison.Ordinal))
            throw new FormatException($"Not a purl: '{purl}'");
        var body = purl[4..];
        var at = body.LastIndexOf('@');
        if (at < 0) throw new FormatException($"Purl without version: '{purl}'");
        var slash = body.IndexOf('/');
        if (slash < 0 || slash > at) throw new FormatException($"Purl without namespace: '{purl}'");
        return new PackageIdentity(body[..slash], body[(slash + 1)..at], body[(at + 1)..], targetOs);
    }
}
