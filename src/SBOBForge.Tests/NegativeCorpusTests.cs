using System;
using System.IO;
using System.Linq;
using SBOBForge.Core;
using SBOBForge.Core.Validation;
using Xunit;

namespace SBOBForge.Tests;

/// <summary>
/// M7 negative corpus (SBOB-Forge #6; TDS 14.4): legal and illegal wildcard
/// spellings verified against the upstream SBOB spec v0.0.4 (§5.1, §5.3, §5.8).
/// The corpus pins the two classes of producers' mistakes the spec calls out:
/// three-dot '...' vs U+22EF '⋯', and glob habits (**, mid-label '*') that the
/// DNS token grammar deliberately rejects.
/// </summary>
public class NegativeCorpusTests
{
    private const string Header =
        "apiVersion: spdx.softwarecomposition.kubescape.io/v1beta1\n" +
        "kind: ContainerProfile\n" +
        "metadata:\n" +
        "  name: example-core.linux\n" +
        "  namespace: develapp\n" +
        "  annotations:\n" +
        "    kubescape.io/managed-by: User\n" +
        "spec:\n" +
        "  imageID: ";

    private const string Purl = "pkg:nuget/example.core@1.0.0";

    private static string EgressDns(string names) =>
        Header + Purl + "\n" +
        "  egress:\n    - identifier: api-x-egress\n      type: external\n" +
        $"      dnsNames: [{names}]\n" +
        "      ports:\n        - { port: 443, protocol: TCP }\n";

    // ---- exec args (spec 5.3) -------------------------------------------------

    [Fact]
    public void ArgsRejectsThreeDots()
    {
        var yaml = Purl + "\n  execs:\n    - path: /usr/bin/tool\n      args: [/usr/bin/tool, ...]\n";
        Assert.Contains(SbobValidator.Validate(Header + yaml),
            e => e.Contains("three-dot run") && e.StartsWith("execs.args"));
    }

    [Fact]
    public void ArgsAcceptSingleSegmentWildcardAsDynamicIdentifier()
    {
        // exactly one '⋯' arg = DynamicIdentifier (spec 5.3); not a shell glob
        var yaml = Purl + "\n  execs:\n    - path: /usr/bin/tool\n      args: [/usr/bin/tool, \u22EF]\n";
        Assert.Empty(SbobValidator.Validate(Header + yaml));
    }

    [Theory]
    [InlineData("*")]        // literal asterisk argument, never a wildcard (spec 5)
    [InlineData("*.log")]    // byte-literal '*, no token semantics in args
    [InlineData("\u22EF\u22EF")] // pair = the args-any wildcard (spec 5.3)
    public void ArgsAcceptsLiteralStarAndWildcardPair(string arg)
    {
        var yaml = Purl + "\n  execs:\n    - path: /usr/bin/tool\n      args: [/usr/bin/tool, " + arg + "]\n";
        Assert.Empty(SbobValidator.Validate(Header + yaml));
    }

    // ---- paths (spec 5.1) ------------------------------------------------------

    [Fact]
    public void PathRejectsMidSegmentWildcard()
    {
        var yaml = Purl + "\n  opens:\n    - path: /app/a\u22EFb/config.json\n      flags: [O_RDONLY]\n";
        Assert.Contains(SbobValidator.Validate(Header + yaml),
            e => e.StartsWith("opens.path") && e.Contains("whole path segment"));
    }

    // ---- dnsNames tokens (spec 5.8) -------------------------------------------

    [Theory]
    [InlineData("api.stripe.com.")]          // literal is byte-equality after trailing-dot norm
    [InlineData("api.stripe.com")]
    [InlineData("\"*.api.stripe.com.\"")]    // leading: RFC 4592, exactly one label
    [InlineData("\"mycorp.com.*\"")]         // trailing: one-or-more labels
    [InlineData("\"mycorp.com.*.\"")]
    [InlineData("\"svc.\u22EF.kubernetes.io.\"")] // mid: DynamicIdentifier, one whole label between <a> and <b>
    public void DnsNamesAcceptsTheThreeLegalTokenPositions(string names)
    {
        Assert.Empty(SbobValidator.Validate(EgressDns(names)));
    }

    [Theory]
    [InlineData("\"**.example.com\"")]     // recursive glob: MUST be rejected (spec 5.8)
    [InlineData("\"example.com.**\"")]
    public void DnsNamesRejectsRecursiveGlob(string names)
    {
        Assert.Contains(SbobValidator.Validate(EgressDns(names)),
            e => e.StartsWith("egress.dnsNames") && e.Contains("rejected"));
    }

    [Theory]
    [InlineData("\"a.*.example.com\"")]        // mid-label '*' is non-standard; must be written '⋯'
    [InlineData("\"svc.*x.example.com\"")]     // '*' must occupy a whole dot-label
    public void DnsNamesRejectsMidLabelStar(string names)
    {
        Assert.Contains(SbobValidator.Validate(EgressDns(names)),
            e => e.StartsWith("egress.dnsNames"));
    }

    [Theory]
    [InlineData("\"svc.\u22EF\"")]                 // no <b> label after the mid token
    [InlineData("\"\u22EF.kubernetes.io\"")]       // no <a> label before the mid token
    [InlineData("\"svc.a\u22EFb.kubernetes.io\"")] // token must occupy a whole dot-label
    public void DnsNamesRejectsWrongShapedMidToken(string names)
    {
        Assert.Contains(SbobValidator.Validate(EgressDns(names)),
            e => e.StartsWith("egress.dnsNames") && e.Contains("<a>.⋯.<b>"));
    }

    [Fact]
    public void DnsNamesThreeDotRunIsRejected()
    {
        Assert.Contains(SbobValidator.Validate(EgressDns("\"svc...kubernetes.io\"")),
            e => e.StartsWith("egress.dnsNames") && e.Contains("three-dot run"));
    }

    // ---- committed goldens corpus ------------------------------------------------

    /// <summary>
    /// NULL vs NONE (spec 5.4): a section that carries no evidence must be omitted
    /// entirely by the canonical emitter — never emitted as an empty '[]' list,
    /// which means NONE ("no traffic intended") and would shadow every observation
    /// under a verifier. TDS 14.4 runs this over every committed fixture golden.
    /// </summary>
    [Fact]
    public void CommittedGoldensNeverEmitNoneSections()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "SBOBForge.sln")))
            repo = repo.Parent!;
        Assert.NotNull(repo);

        var goldens = Directory.EnumerateFiles(
            Path.Combine(repo!.FullName, "examples"), "*.sbob.yaml", SearchOption.AllDirectories).ToList();
        Assert.True(goldens.Count >= 13, $"expected the 13-fixture corpus, found {goldens.Count}");

        foreach (var golden in goldens)
        {
            var yaml = File.ReadAllText(golden);
            Assert.DoesNotContain(": []", yaml);
            var errors = SbobValidator.Validate(yaml);
            Assert.True(errors.Count == 0,
                $"{golden}: {string.Join("; ", errors)}");
        }
    }
}
