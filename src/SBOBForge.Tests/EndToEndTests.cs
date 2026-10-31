using System;
using System.IO;
using System.Linq;
using SBOBForge.Core;
using SBOBForge.Cli;
using SBOBForge.Core.Config;
using SBOBForge.Core.Model;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// End-to-end: the committed examples/dotnet-xunit-fixture must regenerate
/// byte-identically (org decision #26 drift gate) and the pipeline must be
/// deterministic across runs.
/// </summary>
public class EndToEndTests
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
        var repo = FindRepoRoot();
        var fixture = Path.Combine(repo, "examples", "dotnet-xunit-fixture");
        var temp = Path.Combine(Path.GetTempPath(), "sbobforge-fixture-" + Guid.NewGuid().ToString("N"));
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

    private static GenerationResult Generate(string root, bool strict = false) =>
        GeneratePipeline.Run(new GenerationOptions
        {
            RepoRoot = root,
            ConfigPath = Path.Combine(root, "sbobforge.json"),
            LockPath = Path.Combine(root, "sbobforge.lock.json"),
            Strict = strict,
            GeneratorVersion = Program.Version
        });

    [Fact]
    public void FixturePipelineLiftsExpectedSections()
    {
        using var temp = new TempDir(CopyFixtureToTemp());
        var result = Generate(temp.Path);

        // opens: tier-P policy path, tier-L literal paths (SUT + test-attributed), theory-case paths
        Assert.Equal(4, result.Ir.Opens.Count);
        Assert.Contains(result.Ir.Opens, o => o.Path == "/app/⋯/cache/last.json"
            && o.Tier == ResolutionTier.Pattern
            && o.Flags.SequenceEqual(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }));
        var config = Assert.Single(result.Ir.Opens, o => o.Path == "/etc/app/config.json");
        Assert.Equal(ResolutionTier.Literal, config.Tier);
        Assert.Equal(new[] { "O_RDONLY" }, config.Flags);
        // SUT-direct and one-hop test attribution merge into one entry with two provenance rows
        Assert.Equal(2, config.Provenance.Count);
        Assert.Contains(config.Provenance, p => p.TestId.StartsWith("sut:"));
        Assert.Contains(config.Provenance, p => p.TestId.EndsWith("LoadsConfigFromEtc"));
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/app.log");
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/other.log");

        // execs: bare name 'git' must be policy-mapped, never host-PATH resolved
        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/usr/bin/git", exec.Path);
        Assert.Equal(new[] { "/usr/bin/git", "status" }, exec.Args);
        Assert.Equal(ResolutionTier.Pattern, exec.Tier);

        // network: literal https egress with default port; literal TcpListener ingress
        var egress = Assert.Single(result.Ir.Egress);
        Assert.Equal("api.example.com", egress.DnsName);
        Assert.Equal(443, egress.Port);
        var ingress = Assert.Single(result.Ir.Ingress);
        Assert.Equal(8080, ingress.Port);

        // coverage: interpolated path quarantined, File.Delete unrepresentable, skipped test noise
        Assert.Contains(result.Ir.Coverage.Quarantined,
            q => q.Reason.Contains("interpolated") && q.File.EndsWith("ConfigStore.cs"));
        Assert.Contains(result.Ir.Coverage.Unrepresentable, u => u.Primitive == "delete");
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("LegacyPathStillUsed"));
    }

    [Fact]
    public void FixtureRegeneratesByteIdenticallyAcrossRuns()
    {
        using var temp = new TempDir(CopyFixtureToTemp());
        var first = Generate(temp.Path);
        var second = Generate(temp.Path);
        Assert.Equal(first.SbobYaml, second.SbobYaml);
        Assert.Equal(first.ProvenanceJsonl, second.ProvenanceJsonl);
        Assert.Equal(first.CoverageJson, second.CoverageJson);
        Assert.Equal(first.Ir.ComputeBehaviorHash(), second.Ir.ComputeBehaviorHash());
    }

    [Fact]
    public void CommittedGoldenDoesNotDrift()
    {
        var repo = FindRepoRoot();
        var goldenPath = Path.Combine(repo, "examples", "dotnet-xunit-fixture", "sbob", "example-core.linux.sbob.yaml");
        if (!File.Exists(goldenPath))
            return; // golden not committed yet in this checkout; CI --check covers the committed state

        using var temp = new TempDir(CopyFixtureToTemp());
        var result = Generate(temp.Path);
        Assert.Equal(File.ReadAllText(goldenPath), result.SbobYaml);
    }

    [Fact]
    public void StrictModeFailsWhenQuarantineIsNonEmpty()
    {
        using var temp = new TempDir(CopyFixtureToTemp());
        Assert.Throws<StrictQuarantineException>(() => Generate(temp.Path, strict: true));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path) => Path = path;
        public string Path { get; }

        public void Dispose()
        {
            if (System.IO.Path.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
