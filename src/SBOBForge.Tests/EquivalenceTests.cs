using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SBOBForge.Cli;
using SBOBForge.Core.Model;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// Cross-language equivalence corpus (TDS 14.3, SBOB-Forge #6; org decision #26):
/// the same behavioral scenario expressed in every language that can natively
/// express it must yield byte-identical emitted SBoBs. This is the strongest
/// guard against language-adapter drift: any divergence in catalogs, parsers or
/// adapters shows up as a behavioral inequality between languages, not just a
/// broken golden.
///
/// Scenarios live in <c>equivalence/scenarios/&lt;scenario&gt;/</c> as per-language
/// mini-repos (<c>&lt;lang&gt;/sut</c> + <c>&lt;lang&gt;/tests</c>) sharing one
/// all-literal <c>sbobforge.json</c> (no policies except execPathPolicy where the
/// scenario requires it). Participation is per scenario: a language joins only
/// when its catalog has a native (non-shell-mediated) rule for the scenario
/// primitive — shell-only idioms (e.g. C++ std::system exec) and missing catalog
/// coverage (e.g. no bash/powershell/php ingress rule) are honest exclusions
/// recorded in each scenario directory's README, never forced into the corpus.
/// </summary>
public class EquivalenceTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found (missing SBOBForge.sln)");
    }

    private static string ScenariosRoot() => Path.Combine(FindRepoRoot(), "equivalence", "scenarios");

    /// <summary>Scenario folders in ordinal order, only those with a shared config.</summary>
    public static IEnumerable<object[]> ScenarioNames()
    {
        var root = ScenariosRoot();
        var names = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "sbobforge.json")))
            .Select(d => Path.GetFileName(d))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n });
        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>The language variants of a scenario, ordinal-ordered.</summary>
    private static string[] LanguageDirs(string scenario) =>
        Directory.EnumerateDirectories(Path.Combine(ScenariosRoot(), scenario))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Materialize the scenario's per-language mini-repo into a temp root: language
    /// sources + the shared config + the canonical lock (copied from a committed
    /// fixture so the grammar-lock annotation is stable across languages).
    /// </summary>
    private static string Materialize(string scenario, string lang)
    {
        var repo = FindRepoRoot();
        var src = Path.Combine(ScenariosRoot(), scenario, lang);
        var temp = Path.Combine(Path.GetTempPath(), "sbobforge-equiv-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(src, temp);
        File.Copy(Path.Combine(ScenariosRoot(), scenario, "sbobforge.json"), Path.Combine(temp, "sbobforge.json"));
        var lockSource = Path.Combine(repo, "examples", "go-testing-fixture", "sbobforge.lock.json");
        File.Copy(lockSource, Path.Combine(temp, "sbobforge.lock.json"));
        return temp;
    }

    private static GenerationResult Generate(string root) => GeneratePipeline.Run(new GenerationOptions
    {
        RepoRoot = root,
        ConfigPath = Path.Combine(root, "sbobforge.json"),
        LockPath = Path.Combine(root, "sbobforge.lock.json"),
        GeneratorVersion = Program.Version
    });

    private sealed record ScenarioOutput(string Lang, string SbobYaml, BehaviorIr Ir);

    private static List<ScenarioOutput> GenerateAll(string scenario) =>
        LanguageDirs(scenario)
            .Select(lang =>
            {
                var temp = Materialize(scenario, lang);
                try
                {
                    var result = Generate(temp);
                    return new ScenarioOutput(lang, result.SbobYaml, result.Ir);
                }
                finally
                {
                    Directory.Delete(temp, recursive: true);
                }
            })
            .ToList();

    /// <summary>Emitted-section identity: execs (path+args), opens (path+flags), ingress/egress (dns+ips+port+protocol).</summary>
    private static string SectionsOf(BehaviorIr ir) => string.Join("\n",
        ir.Execs.Select(e => $"exec {e.Path} [{string.Join(" ", e.Args)}]"),
        ir.Opens.Select(o => $"open {o.Path} [{string.Join(" ", o.Flags)}]"),
        ir.Ingress.Select(n => $"in {n.DnsName} [{string.Join(" ", n.IpAddresses)}] {n.Port} {n.Protocol}"),
        ir.Egress.Select(n => $"out {n.DnsName} [{string.Join(" ", n.IpAddresses)}] {n.Port} {n.Protocol}"));

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void AllLanguagesEmitByteIdenticalSbobs(string scenario)
    {
        var outputs = GenerateAll(scenario);
        Assert.True(outputs.Count >= 2, $"{scenario}: at least two language variants required");

        var baseline = outputs[0];
        foreach (var output in outputs.Skip(1))
        {
            Assert.True(string.Equals(baseline.SbobYaml, output.SbobYaml, StringComparison.Ordinal),
                $"{scenario}: {output.Lang} emitted a different SBoB than {baseline.Lang}\n" +
                $"--- {baseline.Lang} ---\n{baseline.SbobYaml}\n--- {output.Lang} ---\n{output.SbobYaml}");
        }
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void SectionsMatchTheScenarioContract(string scenario)
    {
        foreach (var output in GenerateAll(scenario))
        {
            var ir = output.Ir;
            switch (scenario)
            {
                case "open-read":
                    AssertExpected(ir, opens: (path: "/etc/app/config.json", flags: new[] { "O_RDONLY" }));
                    Assert.Empty(ir.Coverage.Quarantined);
                    break;
                case "open-append":
                    AssertExpected(ir, opens: (path: "/var/log/app.log", flags: new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }));
                    break;
                case "open-write-truncate":
                    AssertExpected(ir, opens: (path: "/var/lib/app/cache.json", flags: new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }));
                    break;
                case "exec-args":
                    var exec = Assert.Single(ir.Execs);
                    Assert.Equal("/usr/bin/git", exec.Path);
                    Assert.Equal(new[] { "/usr/bin/git", "status" }, exec.Args);
                    Assert.Empty(ir.Opens);
                    Assert.Empty(ir.Egress);
                    Assert.Empty(ir.Ingress);
                    break;
                case "https-egress":
                    Assert.Empty(ir.Execs);
                    Assert.Empty(ir.Opens);
                    Assert.Empty(ir.Ingress);
                    var egress = Assert.Single(ir.Egress);
                    Assert.Equal("api.example.com", egress.DnsName);
                    Assert.Equal(443, egress.Port);
                    Assert.Equal("TCP", egress.Protocol);
                    break;
                case "listen-ingress":
                    Assert.Empty(ir.Execs);
                    Assert.Empty(ir.Opens);
                    Assert.Empty(ir.Egress);
                    var ingress = Assert.Single(ir.Ingress);
                    Assert.Equal(8080, ingress.Port);
                    Assert.Equal("TCP", ingress.Protocol);
                    break;
                case "dynamic-open-quarantine":
                    // The interpolated/identifier path must fail closed identically: no
                    // emitted open for it in any language, exactly one quarantined Open
                    // candidate (TDS 7.3 tier Q), while the sibling literal open lifts
                    // normally (otherwise the emitter fail-closes an all-quarantine
                    // profile, SBOB-Forge #49).
                    AssertExpected(ir,
                        opens: (path: "/var/log/app.log", flags: new[] { "O_WRONLY", "O_CREAT", "O_APPEND" }));
                    Assert.NotEmpty(ir.Coverage.Quarantined);
                    Assert.All(ir.Coverage.Quarantined, x => Assert.Equal(BehaviorPrimitive.Open, x.Primitive));
                    Assert.Contains(ir.Coverage.Quarantined, x => x.TestId.Length > 0);
                    break;
                default:
                    Assert.Fail($"unknown scenario contract: {scenario}");
                    break;
            }
        }
    }

    private static void AssertExpected(BehaviorIr ir, (string path, string[] flags)? opens = null)
    {
        Assert.Empty(ir.Execs);
        Assert.Empty(ir.Egress);
        Assert.Empty(ir.Ingress);
        Assert.Empty(ir.Coverage.Unrepresentable);
        if (opens is { } expected)
        {
            var open = Assert.Single(ir.Opens);
            Assert.Equal(expected.path, open.Path);
            Assert.Equal(expected.flags, open.Flags);
        }
        else
        {
            Assert.Empty(ir.Opens);
        }
    }
}
