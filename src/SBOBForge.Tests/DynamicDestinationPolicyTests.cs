using System;
using System.IO;
using System.Linq;
using SBOBForge.Catalog;
using SBOBForge.Cli;
using SBOBForge.Core.Config;
using SBOBForge.Core.Emit;
using SBOBForge.Core.Model;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// Declared dynamic-destination placeholder policies (SBOB-Forge #49): the tier-P
/// mechanism extended from literal path prefixes to identifier-valued
/// destinations. Design decisions (Lars 2026-11-16, recorded on the issue):
/// emit supports the bare '⋯' wildcard and constrained patterns ('*.internal');
/// matching is by variable identity at the quarantine site; lifted candidates
/// keep a coverage row with the policy id as the rule; the public-lane gate
/// rejects wildcard egress unless --allow'ed.
/// </summary>
public class DynamicDestinationPolicyTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>Copy of the committed dotnet fixture reduced to the forwarding shape:
    /// only ForwardingClient (identifier URL egress + identifier open) — every other
    /// SUT/test file is removed so nothing else contributes events.</summary>
    private static string SyntheticForwardingFixture(string configJson)
    {
        var repo = FindRepoRoot();
        var source = Path.Combine(repo, "examples", "dotnet-xunit-fixture");
        var temp = Path.Combine(Path.GetTempPath(), "sbobforge-dynamic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "sut"));
        foreach (var file in new[] { "sbobforge.lock.json" })
            File.Copy(Path.Combine(source, file), Path.Combine(temp, file));
        File.Copy(
            Path.Combine(source, "sut", "ForwardingClient.cs"),
            Path.Combine(temp, "sut", "ForwardingClient.cs"));
        File.WriteAllText(Path.Combine(temp, "sbobforge.json"), configJson);
        return temp;
    }

    private static GenerationResult Generate(string root) =>
        GeneratePipeline.Run(new GenerationOptions
        {
            RepoRoot = root,
            ConfigPath = Path.Combine(root, "sbobforge.json"),
            LockPath = Path.Combine(root, "sbobforge.lock.json"),
            GeneratorVersion = Program.Version
        });

    private const string NoPoliciesConfig = """
        {
          "packageType": "nuget",
          "packageName": "Example.Forwarding",
          "packageVersion": "1.0.0",
          "namespace": "develapp",
          "targetOs": "linux",
          "architectures": [ "amd64" ],
          "sutGlobs": [ "sut/**/*.cs" ],
          "testGlobs": [ "tests/**/*.cs" ],
          "allowParseError": []
        }
        """;

    private const string NetPoliciesConfig = """
        {
          "packageType": "nuget",
          "packageName": "Example.Forwarding",
          "packageVersion": "1.0.0",
          "namespace": "develapp",
          "targetOs": "linux",
          "architectures": [ "amd64" ],
          "sutGlobs": [ "sut/**/*.cs" ],
          "testGlobs": [ "tests/**/*.cs" ],
          "netPolicies": [
            { "id": "forwarded-request-url", "match": "targetUrl", "emit": "⋯" }
          ],
          "allowParseError": []
        }
        """;

    // ---- lowering: the quarantine carries the variable identity -------------------

    private static InvocationDecl Inv(string? receiver, string method, params CallArg[] args) =>
        new(receiver, method, 10, args);

    [Fact]
    public void IdentifierUrlQuarantineCarriesTheIdentifier()
    {
        var lowered = DotNetCatalogLowering.Lower(Inv("http", "GetStringAsync",
            new CallArg(ArgKind.Identifier, "targetUrl")));
        Assert.Equal(LiftOutcome.Quarantine, lowered!.Outcome);
        Assert.Equal(BehaviorPrimitive.Connect, lowered.Primitive);
        Assert.Equal("targetUrl", lowered.ArgIdentifier);
        Assert.Contains("identifier", lowered.Reason);
    }

    [Fact]
    public void IdentifierOpenQuarantineCarriesIdentifierAndRuleFlags()
    {
        var lowered = DotNetCatalogLowering.Lower(Inv("File", "WriteAllText",
            new CallArg(ArgKind.Identifier, "tempRoot"), CallArg.Literal("{}")));
        Assert.Equal(LiftOutcome.Quarantine, lowered!.Outcome);
        Assert.Equal(BehaviorPrimitive.Open, lowered.Primitive);
        Assert.Equal("tempRoot", lowered.ArgIdentifier);
        // the rule's static flags travel with the quarantine so a policy lift keeps them
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, lowered.Flags);
    }

    [Fact]
    public void NonIdentifierQuarantineCarriesNoIdentity()
    {
        var interpolated = DotNetCatalogLowering.Lower(Inv("File", "WriteAllText",
            new CallArg(ArgKind.Interpolated, "$\"/tmp/{name}\""), CallArg.Literal("{}")));
        Assert.Equal(LiftOutcome.Quarantine, interpolated!.Outcome);
        Assert.Null(interpolated.ArgIdentifier);

        var expression = DotNetCatalogLowering.Lower(Inv("File", "WriteAllText",
            new CallArg(ArgKind.Expression, "tempRoot + \"/x\""), CallArg.Literal("{}")));
        Assert.Equal(LiftOutcome.Quarantine, expression!.Outcome);
        Assert.Null(expression.ArgIdentifier);
    }

    // ---- pipeline: without a declared policy, nothing changes ---------------------

    [Fact]
    public void WithoutPolicyTheForwardingPackageFailsSectionShadowed()
    {
        using var temp = new TempDir(SyntheticForwardingFixture(NoPoliciesConfig));
        var ex = Assert.Throws<EmitException>(() => Generate(temp.Path));
        Assert.Contains("Section-shadowed", ex.Message);
    }

    [Fact]
    public void UnmatchedIdentifierStillQuarantines()
    {
        // policy declared for 'targetUrl' only; the SUT also opens an identifier
        // path with no matching pathPolicy — that candidate must stay quarantined.
        using var temp = new TempDir(SyntheticForwardingFixture(NetPoliciesConfig));
        var result = Generate(temp.Path);
        Assert.Contains(result.Ir.Coverage.Quarantined, q =>
            q.Primitive == BehaviorPrimitive.Open
            && q.Reason.Contains("identifier")
            && q.TestId.StartsWith("sut:Example.Core.Sut.ForwardingClient.SaveSnapshot"));
    }

    // ---- pipeline: the lifts -------------------------------------------------------

    [Fact]
    public void NetPolicyLiftsIdentifierUrlToTierPWildcardEgress()
    {
        using var temp = new TempDir(SyntheticForwardingFixture(NetPoliciesConfig));
        var result = Generate(temp.Path);

        var egress = Assert.Single(result.Ir.Egress);
        Assert.Equal("⋯", egress.DnsName);
        Assert.Null(egress.Port);
        Assert.Equal(NetDirection.Egress, egress.Direction);
        Assert.Equal(ResolutionTier.Pattern, egress.Tier);
        // Lars 2026-11-16: lifted candidates keep a coverage row — tier P with
        // the policy id as the rule
        var provenance = Assert.Single(egress.Provenance);
        Assert.Equal(ResolutionTier.Pattern, provenance.Tier);
        Assert.Equal("forwarded-request-url", provenance.CatalogRuleId);
        Assert.Equal("sut:Example.Core.Sut.ForwardingClient.Forward", provenance.TestId);

        Assert.Contains("dnsNames: [\"⋯\"]", result.SbobYaml);
        Assert.DoesNotContain("ports:", result.SbobYaml.Split("egress:", 2)[1]);
    }

    [Fact]
    public void NetPolicyConstrainedPatternEmitsTheDeclaredSuffix()
    {
        var config = NetPoliciesConfig.Replace("\"emit\": \"⋯\"", "\"emit\": \"*.internal\"");
        using var temp = new TempDir(SyntheticForwardingFixture(config));
        var result = Generate(temp.Path);
        var egress = Assert.Single(result.Ir.Egress);
        Assert.Equal("*.internal", egress.DnsName);
        Assert.Contains("dnsNames: [\"*.internal\"]", result.SbobYaml);
    }

    [Fact]
    public void PathPolicyLiftsIdentifierOpenToTierPWildcardWithRuleFlags()
    {
        var config = NetPoliciesConfig.Replace("\"allowParseError\": []",
            "\"pathPolicies\": [ { \"id\": \"snapshot-temp-dir\", \"match\": \"tempRoot\", \"emit\": \"/tmp/⋯\" } ],\n  \"allowParseError\": []");
        using var temp = new TempDir(SyntheticForwardingFixture(config));
        var result = Generate(temp.Path);

        var open = Assert.Single(result.Ir.Opens);
        Assert.Equal("/tmp/⋯", open.Path);
        Assert.Equal(ResolutionTier.Pattern, open.Tier);
        Assert.Equal(new[] { "O_WRONLY", "O_CREAT", "O_TRUNC" }, open.Flags);
        Assert.Equal("snapshot-temp-dir", Assert.Single(open.Provenance).CatalogRuleId);
        Assert.Empty(result.Ir.Coverage.Quarantined);
    }

    [Fact]
    public void PathPolicyWithoutIdFailsClosedWhenItWouldLift()
    {
        var config = NetPoliciesConfig.Replace("\"allowParseError\": []",
            "\"pathPolicies\": [ { \"match\": \"tempRoot\", \"emit\": \"/tmp/⋯\" } ],\n  \"allowParseError\": []");
        using var temp = new TempDir(SyntheticForwardingFixture(config));
        var ex = Assert.Throws<InvalidDataException>(() => Generate(temp.Path));
        Assert.Contains("must declare an id", ex.Message);
        Assert.Contains("tempRoot", ex.Message);
    }

    [Fact]
    public void LiftedProfileIsDeterministicAcrossRuns()
    {
        using var temp = new TempDir(SyntheticForwardingFixture(NetPoliciesConfig));
        var first = Generate(temp.Path);
        var second = Generate(temp.Path);
        Assert.Equal(first.SbobYaml, second.SbobYaml);
        Assert.Equal(first.ProvenanceJsonl, second.ProvenanceJsonl);
        Assert.Equal(first.CoverageJson, second.CoverageJson);
        Assert.Equal(first.Ir.ComputeBehaviorHash(), second.Ir.ComputeBehaviorHash());
    }

    // ---- config validation (fail-closed) -------------------------------------------

    [Fact]
    public void NetPoliciesValidateAtConfigLoad()
    {
        using var temp = new TempDir(SyntheticForwardingFixture(NoPoliciesConfig));
        var configPath = Path.Combine(temp.Path, "sbobforge.json");
        var baseJson = File.ReadAllText(configPath);

        void AssertInvalid(string json, string expectedFragment)
        {
            File.WriteAllText(configPath, json);
            var ex = Assert.Throws<InvalidDataException>(() => SbobForgeConfig.Load(configPath));
            Assert.Contains(expectedFragment, ex.Message);
        }

        AssertInvalid(baseJson.Replace("\"allowParseError\": []",
            "\"netPolicies\": [ { \"match\": \"u\", \"emit\": \"⋯\" } ], \"allowParseError\": []"), "non-blank id");
        AssertInvalid(baseJson.Replace("\"allowParseError\": []",
            "\"netPolicies\": [ { \"id\": \"p1\", \"emit\": \"⋯\" } ], \"allowParseError\": []"), "non-blank match");
        AssertInvalid(baseJson.Replace("\"allowParseError\": []",
            "\"netPolicies\": [ { \"id\": \"p1\", \"match\": \"u\" } ], \"allowParseError\": []"), "non-blank emit");
        AssertInvalid(baseJson.Replace("\"allowParseError\": []",
            "\"netPolicies\": [ { \"id\": \"p1\", \"match\": \"u\", \"emit\": \"https://x.example.com\" } ], \"allowParseError\": []"),
            "not a DNS-name pattern");
        AssertInvalid(baseJson.Replace("\"allowParseError\": []",
            "\"netPolicies\": [ { \"id\": \"p\", \"match\": \"u\", \"emit\": \"⋯\" }, { \"id\": \"p\", \"match\": \"v\", \"emit\": \"⋯\" } ], \"allowParseError\": []"),
            "more than once");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string path) => Path = path;
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
