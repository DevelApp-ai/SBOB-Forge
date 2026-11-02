using System;
using System.Linq;
using SBOBForge.Core.Emit;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// Public-lane content gate (TDS 10.5): mechanical disclosure check over network
/// targets. Loopback is allowed (universal test fixture, zero estate topology);
/// private DNS suffixes, RFC1918/ULA/link-local/CGNAT ranges, and bare
/// single-label hostnames fail. Overrides are exact-value or dot-suffix.
/// </summary>
public class LaneGateTests
{
    private const string Header =
        "apiVersion: spdx.softwarecomposition.kubescape.io/v1beta1\n" +
        "kind: ContainerProfile\n" +
        "metadata:\n" +
        "  name: example-core.linux\n" +
        "  namespace: develapp\n" +
        "  annotations:\n" +
        "    kubescape.io/managed-by: User\n" +
        "    develapp.io/binding: package-purl\n" +
        "spec:\n" +
        "  architectures: [amd64]\n" +
        "  imageID: pkg:nuget/example.core@1.0.0\n";

    private static string Egress(params string[] dnsOrIps)
    {
        var dns = dnsOrIps.Where(v => !System.Net.IPAddress.TryParse(v, out _)).ToArray();
        var ips = dnsOrIps.Where(v => System.Net.IPAddress.TryParse(v, out _)).ToArray();
        var sb = Header + "  egress:\n    - identifier: x-egress\n      type: external\n";
        if (dns.Length > 0) sb += "      dnsNames: [" + string.Join(", ", dns) + "]\n";
        if (ips.Length > 0) sb += "      ipAddresses: [" + string.Join(", ", ips) + "]\n";
        sb += "      ports:\n        - { port: 443, protocol: TCP }\n";
        return sb;
    }

    [Fact]
    public void PublicDnsAndPublicIpsPass()
    {
        var yaml = Egress("api.example.com", "api.github.com", "8.8.8.8");
        Assert.Empty(Core.Validation.PublicLaneGate.Check(yaml));
    }

    [Fact]
    public void LoopbackIsAllowedByDefault()
    {
        var yaml = Egress("localhost", "127.0.0.1", "::1");
        Assert.Empty(Core.Validation.PublicLaneGate.Check(yaml));
    }

    [Theory]
    [InlineData("host.internal")]
    [InlineData("host.intranet")]
    [InlineData("db.corp")]
    [InlineData("build.lan")]
    [InlineData("printer.local")]
    [InlineData("vault.private")]
    [InlineData("nas.home")]
    public void PrivateSuffixesFail(string host)
    {
        var violations = Core.Validation.PublicLaneGate.Check(Egress(host));
        var v = Assert.Single(violations);
        Assert.Equal("egress", v.Section);
        Assert.Equal("dnsNames", v.Field);
        Assert.Contains("private DNS suffix", v.Rule);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("172.31.255.4")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.7.7")]
    [InlineData("100.64.0.1")]
    [InlineData("fd12:3456:7890::1")]
    [InlineData("fe80::1")]
    public void InternalIpRangesFail(string ip)
    {
        var violations = Core.Validation.PublicLaneGate.Check(Egress(ip));
        var v = Assert.Single(violations);
        Assert.Equal("ipAddresses", v.Field);
    }

    [Fact]
    public void BareSingleLabelHostnameFails()
    {
        var violations = Core.Validation.PublicLaneGate.Check(Egress("mydb"));
        var v = Assert.Single(violations);
        Assert.Contains("single-label", v.Rule);
    }

    [Fact]
    public void CaseInsensitiveRulesAndOverrides()
    {
        var yaml = Egress("DB.CORP");
        Assert.Single(Core.Validation.PublicLaneGate.Check(yaml));
        Assert.Empty(Core.Validation.PublicLaneGate.Check(yaml, new[] { "db.corp" }));
    }

    [Fact]
    public void SuffixOverrideCoversSubdomain()
    {
        var yaml = Egress("ci.dev.corp", "build.dev.corp");
        Assert.Empty(Core.Validation.PublicLaneGate.Check(yaml, new[] { ".dev.corp" }));
        // the override must not over-match a sibling suffix
        Assert.Single(Core.Validation.PublicLaneGate.Check(Egress("ci.prod.corp"), new[] { ".dev.corp" }));
    }

    [Fact]
    public void ExactIpOverride()
    {
        Assert.Empty(Core.Validation.PublicLaneGate.Check(Egress("10.0.0.5"), new[] { "10.0.0.5" }));
    }

    [Fact]
    public void IngressSectionIsGatedToo()
    {
        var yaml = Header +
            "  ingress:\n" +
            "    - identifier: x-ingress\n" +
            "      type: external\n" +
            "      dnsNames: [internal.develapp.local]\n" +
            "      ports:\n" +
            "        - { port: 8080, protocol: TCP }\n";
        var v = Assert.Single(Core.Validation.PublicLaneGate.Check(yaml));
        Assert.Equal("ingress", v.Section);
    }

    [Fact]
    public void SectionScopingEndsAtNextTopLevelKey()
    {
        // dnsNames outside ingress/egress (e.g. under a future top-level block) is not gated.
        var yaml = Egress("api.example.com") +
            "rulePolicies:\n" +
            "  dnsNames: [host.internal]\n";
        Assert.Empty(Core.Validation.PublicLaneGate.Check(yaml));
    }

    [Fact]
    public void QuotedScalarsAreUnquotedBeforeChecking()
    {
        var yaml = Egress("\"host.internal\"");
        Assert.Single(Core.Validation.PublicLaneGate.Check(yaml));
    }

    [Fact]
    public void SameVerdictInEitherDialect()
    {
        var yaml = Egress("host.internal");
        Assert.Single(Core.Validation.PublicLaneGate.Check(yaml));
        Assert.Single(Core.Validation.PublicLaneGate.Check(DialectConverter.ToSbobDialect(yaml)));
    }

    [Fact]
    public void EveryCommittedFixtureGoldenIsPublicLaneClean()
    {
        // Every committed golden must pass the public-lane gate: the fixtures are
        // our own published dogfood and double as the gate's regression corpus.
        var dir = (System.IO.DirectoryInfo?)new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new System.IO.FileNotFoundException("SBOBForge.sln not found in any parent of the test assembly directory");
        var examples = System.IO.Path.Combine(dir.FullName, "examples");
        var goldenCount = 0;
        foreach (var golden in System.IO.Directory.EnumerateFiles(examples, "*.sbob.yaml", System.IO.SearchOption.AllDirectories))
        {
            goldenCount++;
            var violations = Core.Validation.PublicLaneGate.Check(System.IO.File.ReadAllText(golden));
            Assert.True(violations.Count == 0,
                $"{golden}: {string.Join("; ", violations.Select(v => $"{v.Section}.{v.Field} '{v.Value}' — {v.Rule}"))}");
        }
        Assert.True(goldenCount >= 13, $"expected at least 13 fixture goldens, found {goldenCount}");
    }
}
