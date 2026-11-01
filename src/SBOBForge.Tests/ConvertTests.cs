using System;
using System.Linq;
using SBOBForge.Core;
using SBOBForge.Core.Emit;
using Xunit;

namespace SBOBForge.Tests;

public class ConvertTests
{
    private const string Canonical =
        "apiVersion: spdx.softwarecomposition.kubescape.io/v1beta1\n" +
        "kind: ContainerProfile\n" +
        "metadata:\n" +
        "  name: example-core.linux\n" +
        "  namespace: develapp\n" +
        "  annotations:\n" +
        "    kubescape.io/managed-by: User\n" +
        "    develapp.io/binding: package-purl\n" +
        "    develapp.io/generator: sbobforge@0.1.0\n" +
        "    develapp.io/grammar-lock-sha256: deadbeef\n" +
        "    develapp.io/behavior-hash: 4b9aef2051a80e93fac0f7d13aad127bc9821cb3d456cf4c86b7c4c09de6fafe\n" +
        "spec:\n" +
        "  architectures: [amd64]\n" +
        "  imageID: pkg:nuget/example.core@1.0.0\n" +
        "  execs:\n" +
        "    - path: /usr/bin/git\n" +
        "      args: [/usr/bin/git, status]\n" +
        "  opens:\n" +
        "    - path: /etc/app/config.json\n" +
        "      flags: [O_RDONLY]\n" +
        "  egress:\n" +
        "    - identifier: api.example.com-egress\n" +
        "      type: external\n" +
        "      dnsNames: [api.example.com]\n" +
        "      ports:\n" +
        "        - { port: 443, protocol: TCP }\n";

    [Fact]
    public void SwapsHeaderAndAnnotationMarker()
    {
        var converted = DialectConverter.ToSbobDialect(Canonical);

        var expected =
            "apiVersion: sbob.k8sstormcenter.io/v1\n" +
            "kind: SoftwareBillOfBehavior\n" +
            "metadata:\n" +
            "  name: example-core.linux\n" +
            "  namespace: develapp\n" +
            "  annotations:\n" +
            "    sbob.spec-version: \"0.0.4\"\n" +
            "    develapp.io/binding: package-purl\n" +
            "    develapp.io/generator: sbobforge@0.1.0\n" +
            "    develapp.io/grammar-lock-sha256: deadbeef\n" +
            "    develapp.io/behavior-hash: 4b9aef2051a80e93fac0f7d13aad127bc9821cb3d456cf4c86b7c4c09de6fafe\n" +
            "spec:\n" +
            "  architectures: [amd64]\n" +
            "  imageID: pkg:nuget/example.core@1.0.0\n" +
            "  execs:\n" +
            "    - path: /usr/bin/git\n" +
            "      args: [/usr/bin/git, status]\n" +
            "  opens:\n" +
            "    - path: /etc/app/config.json\n" +
            "      flags: [O_RDONLY]\n" +
            "  egress:\n" +
            "    - identifier: api.example.com-egress\n" +
            "      type: external\n" +
            "      dnsNames: [api.example.com]\n" +
            "      ports:\n" +
            "        - { port: 443, protocol: TCP }\n";
        Assert.Equal(expected, converted);
    }

    [Fact]
    public void ConversionIsDeterministic()
    {
        Assert.Equal(DialectConverter.ToSbobDialect(Canonical), DialectConverter.ToSbobDialect(Canonical));
    }

    [Fact]
    public void BehaviorHashAnnotationSurvivesConversion()
    {
        var hashLine = Canonical.Split('\n').First(l => l.Contains("develapp.io/behavior-hash"));
        var convertedHashLine = DialectConverter.ToSbobDialect(Canonical).Split('\n').First(l => l.Contains("develapp.io/behavior-hash"));
        Assert.Equal(hashLine, convertedHashLine);
    }

    [Fact]
    public void RejectsWrongApiVersion()
    {
        var ex = Assert.Throws<ParseHardError>(() =>
            DialectConverter.ToSbobDialect(Canonical.Replace("spdx.softwarecomposition.kubescape.io/v1beta1", "sbob.k8sstormcenter.io/v1")));
        Assert.Contains("kubescape-dialect", ex.Message);
    }

    [Fact]
    public void RejectsWrongKind()
    {
        Assert.Throws<ParseHardError>(() =>
            DialectConverter.ToSbobDialect(Canonical.Replace("kind: ContainerProfile", "kind: SoftwareBillOfBehavior")));
    }

    [Fact]
    public void RejectsMissingManagedByAnnotation()
    {
        Assert.Throws<ParseHardError>(() =>
            DialectConverter.ToSbobDialect(Canonical.Replace("    kubescape.io/managed-by: User\n", "")));
    }

    [Fact]
    public void RejectsEmptyInput()
    {
        Assert.Throws<ParseHardError>(() => DialectConverter.ToSbobDialect(""));
    }

    [Fact]
    public void ConvertsEveryCommittedFixtureGolden()
    {
        // Every kubescape-dialect golden in the repo converts cleanly and the
        // payload below the annotation block is byte-identical (replay-lane
        // contract: the registry file differs from the emitted profile only in
        // the header and the dialect-marker annotation).
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "SBOBForge.sln")))
            dir = dir.Parent!;
        var examples = System.IO.Path.Combine(dir.FullName, "examples");
        var goldenCount = 0;
        foreach (var golden in System.IO.Directory.EnumerateFiles(examples, "*.sbob.yaml", System.IO.SearchOption.AllDirectories))
        {
            goldenCount++;
            var canonical = System.IO.File.ReadAllText(golden);
            var converted = DialectConverter.ToSbobDialect(canonical);

            Assert.StartsWith("apiVersion: sbob.k8sstormcenter.io/v1\nkind: SoftwareBillOfBehavior\n", converted);
            Assert.DoesNotContain("kubescape.io/managed-by", converted);
            Assert.Contains("sbob.spec-version: \"0.0.4\"", converted);
            Assert.Contains("develapp.io/behavior-hash:", converted);

            // payload below the annotation marker is untouched
            var canonicalTail = canonical.Substring(canonical.IndexOf("    develapp.io/binding: package-purl\n"));
            var convertedTail = converted.Substring(converted.IndexOf("    develapp.io/binding: package-purl\n"));
            Assert.Equal(canonicalTail, convertedTail);
        }
        Assert.True(goldenCount >= 13, $"expected at least 13 fixture goldens, found {goldenCount}");
    }
}
