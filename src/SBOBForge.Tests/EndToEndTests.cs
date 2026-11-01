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

    private static string CopyFixtureToTemp(string fixtureName = "dotnet-xunit-fixture")
    {
        var repo = FindRepoRoot();
        var fixture = Path.Combine(repo, "examples", fixtureName);
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

    // ---- M2: Java and Python fixtures (#7) -------------------------------

    [Theory]
    [InlineData("java-junit5-fixture")]
    [InlineData("java-testng-fixture")]
    [InlineData("python-pytest-fixture")]
    [InlineData("python-unittest-fixture")]
    public void M2FixtureRegeneratesByteIdenticallyAndDoesNotDrift(string fixtureName)
    {
        using var temp = new TempDir(CopyFixtureToTemp(fixtureName));
        var first = Generate(temp.Path);
        var second = Generate(temp.Path);
        Assert.Equal(first.SbobYaml, second.SbobYaml);
        Assert.Equal(first.ProvenanceJsonl, second.ProvenanceJsonl);
        Assert.Equal(first.CoverageJson, second.CoverageJson);
        Assert.Equal(first.Ir.ComputeBehaviorHash(), second.Ir.ComputeBehaviorHash());

        var repo = FindRepoRoot();
        var golden = Path.Combine(repo, "examples", fixtureName, "sbob", first.SbobFileName);
        if (File.Exists(golden))
            Assert.Equal(File.ReadAllText(golden), first.SbobYaml);
    }

    [Fact]
    public void JavaJunit5FixtureLiftsExpectedSections()
    {
        using var temp = new TempDir(CopyFixtureToTemp("java-junit5-fixture"));
        var result = Generate(temp.Path);

        Assert.Equal(4, result.Ir.Opens.Count);
        Assert.Contains(result.Ir.Opens, o => o.Path == "/app/⋯/cache/last.json"
            && o.Tier == ResolutionTier.Pattern
            && o.Flags.SequenceEqual(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }));
        var config = Assert.Single(result.Ir.Opens, o => o.Path == "/etc/app/config.json");
        Assert.Equal(ResolutionTier.Literal, config.Tier);
        Assert.Equal(new[] { "O_RDONLY" }, config.Flags);
        // SUT-direct (Files.readString) + one-hop test attribution merge into one entry
        Assert.Equal(2, config.Provenance.Count);
        Assert.Contains(config.Provenance, p => p.TestId.StartsWith("sut:"));
        Assert.Contains(config.Provenance, p => p.TestId.EndsWith("loadsConfigFromEtc"));
        // @ValueSource theory propagation through Path.of(path)
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/app.log");
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/other.log");

        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/usr/bin/git", exec.Path);
        Assert.Equal(new[] { "/usr/bin/git", "status" }, exec.Args);
        Assert.Equal(ResolutionTier.Pattern, exec.Tier);

        Assert.Equal("api.example.com", Assert.Single(result.Ir.Egress).DnsName);
        Assert.Equal(443, Assert.Single(result.Ir.Egress).Port);
        Assert.Equal(8080, Assert.Single(result.Ir.Ingress).Port);

        Assert.Contains(result.Ir.Coverage.Quarantined,
            q => q.Reason.Contains("non-constant expression") && q.File.EndsWith("ConfigStore.java"));
        Assert.Contains(result.Ir.Coverage.Unrepresentable, u => u.Primitive == "delete");
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("legacyPathStillUsed"));
    }

    [Fact]
    public void JavaTestngFixtureLiftsAppendAndTruncate()
    {
        using var temp = new TempDir(CopyFixtureToTemp("java-testng-fixture"));
        var result = Generate(temp.Path);

        // FileOutputStream(path, true) → O_APPEND; FileOutputStream(path) → O_TRUNC
        var append = Assert.Single(result.Ir.Opens,
            o => o.Path == "/var/log/ng-app.log" && o.Flags.Contains("O_APPEND"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, append.Flags);
        // SUT-direct + enabled @Test one-hop call
        Assert.Equal(2, append.Provenance.Count);
        var truncate = Assert.Single(result.Ir.Opens,
            o => o.Path == "/var/log/ng-app.log" && o.Flags.Contains("O_TRUNC"));
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, truncate.Flags);
        // rewriteLog is only called by the disabled test → SUT-direct provenance only
        Assert.Single(truncate.Provenance, p => p.TestId.StartsWith("sut:"));

        // Runtime.getRuntime().exec("git status") → token split + execPathPolicy
        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/usr/bin/git", exec.Path);
        Assert.Equal(new[] { "/usr/bin/git", "status" }, exec.Args);

        var egress = Assert.Single(result.Ir.Egress);
        Assert.Equal("api.example.com", egress.DnsName);
        Assert.Equal(443, egress.Port);
        Assert.Empty(result.Ir.Ingress);
        Assert.Empty(result.Ir.Coverage.Quarantined);
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("legacyRewriteStillUsed"));
    }

    [Fact]
    public void PythonPytestFixtureLiftsExpectedSections()
    {
        using var temp = new TempDir(CopyFixtureToTemp("python-pytest-fixture"));
        var result = Generate(temp.Path);

        Assert.Equal(4, result.Ir.Opens.Count);
        var cache = Assert.Single(result.Ir.Opens, o => o.Path == "/app/⋯/cache/last.json");
        Assert.Equal(ResolutionTier.Pattern, cache.Tier);
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, cache.Flags);

        var config = Assert.Single(result.Ir.Opens, o => o.Path == "/etc/app/config.json");
        Assert.Equal(new[] { "O_RDONLY" }, config.Flags);
        Assert.Equal(2, config.Provenance.Count);
        Assert.Contains(config.Provenance, p => p.TestId.StartsWith("sut:"));
        Assert.Contains(config.Provenance, p => p.TestId.EndsWith("test_loads_config"));

        // parametrize case values bound by parameter name into open(path, "w")
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/app.log"
            && o.Flags.SequenceEqual(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }));
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/other.log");

        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/usr/bin/git", exec.Path);
        Assert.Equal(new[] { "/usr/bin/git", "status" }, exec.Args);

        Assert.Equal("api.example.com", Assert.Single(result.Ir.Egress).DnsName);
        Assert.Equal(443, Assert.Single(result.Ir.Egress).Port);
        Assert.Equal(8080, Assert.Single(result.Ir.Ingress).Port);

        Assert.Contains(result.Ir.Coverage.Quarantined,
            q => q.Reason.Contains("interpolated") && q.File.EndsWith("config_store.py"));
        Assert.Contains(result.Ir.Coverage.Unrepresentable, u => u.Primitive == "delete");
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("test_legacy_path"));
    }

    [Fact]
    public void PythonUnittestFixtureLiftsShellExecAndModeStrings()
    {
        using var temp = new TempDir(CopyFixtureToTemp("python-unittest-fixture"));
        var result = Generate(temp.Path);

        // os.makedirs("%PackageRoot%/cache") → tier-P directory open
        var cache = Assert.Single(result.Ir.Opens, o => o.Path == "/app/⋯/cache");
        Assert.Equal(ResolutionTier.Pattern, cache.Tier);
        Assert.Equal(new[] { "O_DIRECTORY", "O_CREAT" }, cache.Flags);

        // open(path, "a") mode string → O_APPEND; SUT-direct + skipIf(False) one-hop test
        var log = Assert.Single(result.Ir.Opens, o => o.Path == "/var/log/status.log");
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, log.Flags);
        Assert.Equal(2, log.Provenance.Count);
        Assert.Contains(log.Provenance, p => p.TestId.EndsWith("test_appends_log"));

        // subprocess.run("git status", shell=True) → fixed /bin/sh -c lowering
        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/bin/sh", exec.Path);
        Assert.Equal(new[] { "/bin/sh", "-c", "git status" }, exec.Args);

        var egress = Assert.Single(result.Ir.Egress);
        Assert.Equal("api.example.com", egress.DnsName);
        Assert.Equal(443, egress.Port);
        // HTTPServer(("0.0.0.0", 8080), handler) address tuple
        Assert.Equal(8080, Assert.Single(result.Ir.Ingress).Port);

        Assert.Contains(result.Ir.Coverage.Unrepresentable, u => u.Primitive == "delete");
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("test_legacy_shell"));
    }

    // ---- M3: Go and JS/TS fixtures (#5) ---------------------------------

    [Theory]
    [InlineData("go-testing-fixture")]
    [InlineData("js-jest-fixture")]
    [InlineData("ts-vitest-fixture")]
    public void M3FixtureRegeneratesByteIdenticallyAndDoesNotDrift(string fixtureName)
    {
        using var temp = new TempDir(CopyFixtureToTemp(fixtureName));
        var first = Generate(temp.Path);
        var second = Generate(temp.Path);
        Assert.Equal(first.SbobYaml, second.SbobYaml);
        Assert.Equal(first.ProvenanceJsonl, second.ProvenanceJsonl);
        Assert.Equal(first.CoverageJson, second.CoverageJson);
        Assert.Equal(first.Ir.ComputeBehaviorHash(), second.Ir.ComputeBehaviorHash());

        var repo = FindRepoRoot();
        var golden = Path.Combine(repo, "examples", fixtureName, "sbob", first.SbobFileName);
        if (File.Exists(golden))
            Assert.Equal(File.ReadAllText(golden), first.SbobYaml);
    }

    [Fact]
    public void GoTestingFixtureLiftsExpectedSections()
    {
        using var temp = new TempDir(CopyFixtureToTemp("go-testing-fixture"));
        var result = Generate(temp.Path);

        Assert.Equal(3, result.Ir.Opens.Count);
        // os.OpenFile flag mask (os.O_WRONLY|os.O_CREATE|os.O_APPEND) → canonical flags
        var log = Assert.Single(result.Ir.Opens, o => o.Path == "/var/log/go-app.log");
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, log.Flags);
        Assert.Equal(ResolutionTier.Literal, log.Tier);
        // SUT-direct + one-hop test attribution merge into one entry
        Assert.Equal(2, log.Provenance.Count);
        Assert.Contains(log.Provenance, p => p.TestId.StartsWith("sut:core."));
        Assert.Contains(log.Provenance, p => p.TestId.EndsWith("TestAppendsLog"));

        var config = Assert.Single(result.Ir.Opens, o => o.Path == "/etc/app/config.json");
        Assert.Equal(new[] { "O_RDONLY" }, config.Flags);
        Assert.Equal(2, config.Provenance.Count);

        var cache = Assert.Single(result.Ir.Opens, o => o.Path == "/app/⋯/cache/last.json");
        Assert.Equal(ResolutionTier.Pattern, cache.Tier);

        // exec.Command("git", "status") → policy-mapped argv
        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/usr/bin/git", exec.Path);
        Assert.Equal(new[] { "/usr/bin/git", "status" }, exec.Args);
        Assert.Equal(ResolutionTier.Pattern, exec.Tier);

        Assert.Equal("api.example.com", Assert.Single(result.Ir.Egress).DnsName);
        Assert.Equal(443, Assert.Single(result.Ir.Egress).Port);
        // net.Listen("tcp", ":8080") → ingress with no host
        Assert.Equal(8080, Assert.Single(result.Ir.Ingress).Port);

        // "/etc/app/users/"+user+".json" concatenation quarantines
        Assert.Contains(result.Ir.Coverage.Quarantined,
            q => q.Reason.Contains("non-constant expression") && q.File.EndsWith("configstore.go"));
        // os.Remove → delete primitive, unrepresentable in the SBOB spec
        Assert.Contains(result.Ir.Coverage.Unrepresentable, u => u.Primitive == "delete");
        // t.Skip'd test excluded
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("TestLegacyPath"));
        Assert.Equal(3, result.Ir.Coverage.TestEntries);
    }

    [Fact]
    public void JsJestFixtureLiftsExpectedSections()
    {
        using var temp = new TempDir(CopyFixtureToTemp("js-jest-fixture"));
        var result = Generate(temp.Path);

        Assert.Equal(4, result.Ir.Opens.Count);
        var config = Assert.Single(result.Ir.Opens, o => o.Path == "/etc/app/config.json");
        Assert.Equal(new[] { "O_RDONLY" }, config.Flags);
        Assert.Equal(2, config.Provenance.Count);
        Assert.Contains(config.Provenance, p => p.TestId.StartsWith("sut:"));
        Assert.Contains(config.Provenance, p => p.TestId.EndsWith("loads config"));

        // test.each case values bound by callback parameter into appendFileSync(p, "entry")
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/app.log"
            && o.Flags.SequenceEqual(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }));
        Assert.Contains(result.Ir.Opens, o => o.Path == "/var/log/other.log");

        var cache = Assert.Single(result.Ir.Opens, o => o.Path == "/app/⋯/cache/last.json");
        Assert.Equal(ResolutionTier.Pattern, cache.Tier);

        // destructured `const { execFile } = require("child_process")` → bare call, argv list
        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/usr/bin/git", exec.Path);
        Assert.Equal(new[] { "/usr/bin/git", "status" }, exec.Args);

        // global fetch (bare receiver) → egress; server.listen (any receiver) → ingress
        Assert.Equal("api.example.com", Assert.Single(result.Ir.Egress).DnsName);
        Assert.Equal(443, Assert.Single(result.Ir.Egress).Port);
        Assert.Equal(8080, Assert.Single(result.Ir.Ingress).Port);

        // template literal + identifier paths quarantine; fs.unlinkSync → delete
        Assert.Equal(2, result.Ir.Coverage.Quarantined.Count);
        Assert.Contains(result.Ir.Coverage.Quarantined, q => q.Reason.Contains("interpolated"));
        Assert.Contains(result.Ir.Coverage.Quarantined, q => q.Reason.Contains("identifier"));
        Assert.Contains(result.Ir.Coverage.Unrepresentable, u => u.Primitive == "delete");
        // it.skip excluded
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("legacy path"));
    }

    [Fact]
    public void TsVitestFixtureLiftsExpectedSections()
    {
        using var temp = new TempDir(CopyFixtureToTemp("ts-vitest-fixture"));
        var result = Generate(temp.Path);

        // fs.openSync(path, "a") mode string → append flags; one-hop test attribution
        var log = Assert.Single(result.Ir.Opens, o => o.Path == "/var/log/ts-status.log");
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }, log.Flags);
        Assert.Equal(2, log.Provenance.Count);
        Assert.Contains(log.Provenance, p => p.TestId.EndsWith("appends log"));

        // direct call inside a test callback attributes to the test entry
        var report = Assert.Single(result.Ir.Opens, o => o.Path == "/var/log/report.log");
        Assert.Single(report.Provenance, p => p.TestId.EndsWith("writes report"));

        // import { exec } from "child_process" → /bin/sh -c shell lowering
        var exec = Assert.Single(result.Ir.Execs);
        Assert.Equal("/bin/sh", exec.Path);
        Assert.Equal(new[] { "/bin/sh", "-c", "git status" }, exec.Args);

        Assert.Equal("api.example.com", Assert.Single(result.Ir.Egress).DnsName);
        Assert.Equal(443, Assert.Single(result.Ir.Egress).Port);
        Assert.Equal(8080, Assert.Single(result.Ir.Ingress).Port);

        // it.skipIf(true) proves a skip: excluded, its SUT call (shellOut) never lifted
        Assert.Contains(result.Ir.Coverage.Noise, n => n.TestId.EndsWith("legacy shell"));
        Assert.DoesNotContain(result.Ir.Execs, e => e.Provenance
            .Any(p => p.TestId.EndsWith("legacy shell")));
        // fs.rmSync → delete primitive
        Assert.Contains(result.Ir.Coverage.Unrepresentable, u => u.Primitive == "delete");
        Assert.Equal(3, result.Ir.Coverage.TestEntries);
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
