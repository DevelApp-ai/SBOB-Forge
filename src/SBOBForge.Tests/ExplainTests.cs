using System;
using System.IO;
using System.Linq;
using SBOBForge.Cli;
using SBOBForge.Core;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// M7 `sbobforge explain` (SBOB-Forge #6; TDS 8.3): provenance-sidecar lookup
/// over a generated fixture. Explain is strictly read-only: it must answer
/// identifier → evidence rows (testIds, file:line, tier, catalog rule, pinned
/// grammar versions) deterministically, and report no-match as NotFound.
/// </summary>
public class ExplainTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found (missing SBOBForge.sln)");
    }

    private static string CopyFixtureToTemp()
    {
        var fixture = Path.Combine(FindRepoRoot(), "examples", "dotnet-xunit-fixture");
        var temp = Path.Combine(Path.GetTempPath(), "sbobforge-explain-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixture, temp);
        return temp;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private static (string Root, ExplainResult Result) GenerateAndExplain(string query)
    {
        var root = CopyFixtureToTemp();
        GeneratePipeline.Run(new GenerationOptions
        {
            RepoRoot = root,
            ConfigPath = Path.Combine(root, "sbobforge.json"),
            LockPath = Path.Combine(root, "sbobforge.lock.json"),
            GeneratorVersion = Program.Version
        });
        var result = ExplainPipeline.Run(new ExplainOptions { RepoRoot = root, Query = query });
        return (root, result);
    }

    [Fact]
    public void ExactIdentifierReturnsAllProvenanceRows()
    {
        // /etc/app/config.json has two provenance rows: SUT-direct (sut:) plus
        // the test-attributed read — explain aggregates both, ordinal-sorted.
        var (_, result) = GenerateAndExplain("/etc/app/config.json");
        Assert.False(result.NotFound);
        Assert.True(result.ExactMatch);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal("/etc/app/config.json", r.Identifier));
        Assert.Contains(result.Rows, r => r.TestId.StartsWith("sut:"));
        Assert.Contains(result.Rows, r => r.TestId.EndsWith("LoadsConfigFromEtc"));
        var fileRow = Assert.Single(result.Rows, r => r.TestId.StartsWith("sut:"));
        Assert.Equal("sut/ConfigStore.cs", fileRow.File);
        Assert.True(fileRow.Line > 0);
        Assert.Equal("open", fileRow.Primitive);
    }

    [Fact]
    public void ExactDnsNameAndCaseInsensitiveSubstringFallback()
    {
        // exact match wins over substring; plain 'example' matches via the
        // case-insensitive substring path
        var (root, exact) = GenerateAndExplain("api.example.com");
        Assert.False(exact.NotFound);
        Assert.True(exact.ExactMatch);
        Assert.All(exact.Rows, r => Assert.Equal("api.example.com", r.Identifier));
        _ = root;

        var (_, partial) = GenerateAndExplain("EXAMPLE.COM");
        Assert.False(partial.NotFound);
        Assert.False(partial.ExactMatch);
        Assert.Contains(partial.Rows, r => r.Identifier == "api.example.com");
    }

    [Fact]
    public void UnknownIdentifierIsNotFound()
    {
        var (_, result) = GenerateAndExplain("/no/such/identifier");
        Assert.True(result.NotFound);
        Assert.Empty(result.Rows);
        Assert.EndsWith(".provenance.jsonl", result.ProvenancePath);
    }

    [Fact]
    public void GrammarVersionsComeFromThePinnedLock()
    {
        var (_, result) = GenerateAndExplain("/etc/app/config.json");
        Assert.All(result.Rows, r => Assert.Equal("csharp 4.11.0, dotnet-catalog v1", r.Grammar));
    }

    [Fact]
    public void RenderIsDeterministicAndSaysHowManyFacts()
    {
        var (_, result) = GenerateAndExplain("/etc/app/config.json");
        var first = ExplainPipeline.Render(result);
        var second = ExplainPipeline.Render(result);
        Assert.Equal(first, second);
        Assert.Contains("open /etc/app/config.json", first);
        Assert.Contains("1 test fact(s), 1 SUT site(s)", first);
        Assert.Contains("sut/ConfigStore.cs", first);
        Assert.Contains("grammar: csharp", first);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJsonIsPascalCaseRows()
    {
        var (_, result) = GenerateAndExplain("/etc/app/config.json");
        var json = ExplainPipeline.RenderJson(result);
        Assert.Contains("\"Primitive\": \"open\"", json);
        Assert.Contains("\"Identifier\": \"/etc/app/config.json\"", json);
        Assert.Contains("\"Grammar\"", json);
        Assert.DoesNotContain('\r', json);
    }
}
