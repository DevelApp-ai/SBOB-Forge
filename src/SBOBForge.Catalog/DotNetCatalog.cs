using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SBOBForge.Core.Config;

namespace SBOBForge.Catalog;

/// <summary>One catalog rule: API signature → behavior primitive + defaults (TDS 7.3 catalog).</summary>
public sealed record CatalogRule(
    string Id,
    string Kind,
    string? Receiver,
    string? Method,
    IReadOnlyList<string> Flags,
    string? UnrepresentablePrimitive);

/// <summary>Loaded .NET API catalog (TDS 7.3). Embedded resource; hashable for the grammar lock.</summary>
public sealed record DotNetCatalog(
    string Id,
    int Version,
    IReadOnlyList<CatalogRule> Rules)
{
    private const string ManifestName = "SBOBForge.Catalog.dotnet-catalog.json";

    public static DotNetCatalog LoadDefault()
    {
        var assembly = typeof(DotNetCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ManifestName)
            ?? throw new InvalidDataException($"Embedded catalog resource missing: {ManifestName}");
        using var reader = new StreamReader(stream);
        var catalog = JsonSerializer.Deserialize<DotNetCatalog>(reader.ReadToEnd(), Json.Options)
            ?? throw new InvalidDataException("Empty or invalid dotnet-catalog.json");
        return catalog;
    }

    /// <summary>Content-derived catalog hash — pinned in the grammar lock (TDS 5.3).</summary>
    public static string ComputeDefaultHash()
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var assembly = typeof(DotNetCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ManifestName)
            ?? throw new InvalidDataException($"Embedded catalog resource missing: {ManifestName}");
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
