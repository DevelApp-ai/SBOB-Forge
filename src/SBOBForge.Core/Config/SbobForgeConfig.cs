using System;
using System.Collections.Generic;
using System.IO;
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
        return cfg;
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
