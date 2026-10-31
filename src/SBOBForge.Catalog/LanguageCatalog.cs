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
    IReadOnlyList<string>? Flags,
    string? UnrepresentablePrimitive,
    int? DefaultPort = null,
    string? Protocol = null);

/// <summary>
/// Loaded API catalog for one language (TDS 7.3). Embedded resource;
/// content hash pinned in the grammar lock (TDS 5.3).
/// </summary>
public sealed record LanguageCatalog(
    string Id,
    int Version,
    IReadOnlyList<CatalogRule> Rules)
{
    public static LanguageCatalog Load(string manifestName)
    {
        var assembly = typeof(LanguageCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(manifestName)
            ?? throw new InvalidDataException($"Embedded catalog resource missing: {manifestName}");
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<LanguageCatalog>(reader.ReadToEnd(), Json.Options)
            ?? throw new InvalidDataException($"Empty or invalid catalog: {manifestName}");
    }

    /// <summary>Content-derived catalog hash — pinned in the grammar lock (TDS 5.3).</summary>
    public static string ComputeHash(string manifestName)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var assembly = typeof(LanguageCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(manifestName)
            ?? throw new InvalidDataException($"Embedded catalog resource missing: {manifestName}");
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}

/// <summary>Embedded-catalog accessors per language (TDS 7.3).</summary>
public static class DotNetCatalog
{
    public const string ManifestName = "SBOBForge.Catalog.dotnet-catalog.json";
    public const string LockEntryName = "dotnet-catalog";
    public static LanguageCatalog LoadDefault() => LanguageCatalog.Load(ManifestName);
    public static string ComputeDefaultHash() => LanguageCatalog.ComputeHash(ManifestName);
}

public static class JavaCatalog
{
    public const string ManifestName = "SBOBForge.Catalog.java-catalog.json";
    public const string LockEntryName = "java-catalog";
    public static LanguageCatalog LoadDefault() => LanguageCatalog.Load(ManifestName);
    public static string ComputeDefaultHash() => LanguageCatalog.ComputeHash(ManifestName);
}

public static class PythonCatalog
{
    public const string ManifestName = "SBOBForge.Catalog.python-catalog.json";
    public const string LockEntryName = "python-catalog";
    public static LanguageCatalog LoadDefault() => LanguageCatalog.Load(ManifestName);
    public static string ComputeDefaultHash() => LanguageCatalog.ComputeHash(ManifestName);
}
