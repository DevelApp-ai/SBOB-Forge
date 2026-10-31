using System;
using System.Collections.Generic;
using System.Linq;
using SBOBForge.Core.Emit;
using SBOBForge.Core.Model;
using Xunit;

namespace SBOBForge.Tests;

public class EmitterTests
{
    private static BehaviorIr MakeIr(params OpenEvent[] opens) => new(
        new PackageIdentity("nuget", "Example.Core", "1.0.0", "linux"),
        Array.Empty<ExecEvent>(), opens,
        Array.Empty<NetEvent>(), Array.Empty<NetEvent>(),
        CoverageReport.Empty, "deadbeef", "cafebabe");

    private static OpenEvent Open(string path, params string[] flags) => new(
        path, flags, ResolutionTier.Literal,
        new[] { new Provenance("Ns.T.M", "src/File.cs", 3, ResolutionTier.Literal, "dotnet.file.readalltext") });

    [Fact]
    public void EmitsCanonicalDocumentWithFixedFieldOrder()
    {
        var ir = MakeIr(Open("/etc/app/config.json", "O_RDONLY"));
        var yaml = CanonicalSbobEmitter.Emit(ir, "0.1.0", "develapp");

        var expected =
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
            "    develapp.io/behavior-hash: " + ir.ComputeBehaviorHash() + "\n" +
            "spec:\n" +
            "  architectures: [amd64]\n" +
            "  imageID: pkg:nuget/example.core@1.0.0\n" +
            "  opens:\n" +
            "    - path: /etc/app/config.json\n" +
            "      flags: [O_RDONLY]\n";
        Assert.Equal(expected, yaml);
    }

    [Fact]
    public void EmissionIsOrderIndependent()
    {
        var a = MakeIr(Open("/b.txt", "O_RDONLY"), Open("/a.txt", "O_WRONLY"), Open("/c.txt", "O_RDONLY"));
        var shuffled = MakeIr(Open("/c.txt", "O_RDONLY"), Open("/b.txt", "O_RDONLY"), Open("/a.txt", "O_WRONLY"));
        Assert.Equal(CanonicalSbobEmitter.Emit(a, "0.1.0", "develapp"),
            CanonicalSbobEmitter.Emit(shuffled, "0.1.0", "develapp"));
        Assert.Equal(a.ComputeBehaviorHash(), shuffled.ComputeBehaviorHash());
    }

    [Fact]
    public void IdenticalEventsMergeProvenanceWithoutChangingHash()
    {
        var one = Open("/etc/app/config.json", "O_RDONLY");
        var two = one with { Provenance = new[] { one.Provenance[0], new Provenance("Ns.T.Other", "src/G.cs", 9, ResolutionTier.Literal, "dotnet.file.readalltext") } };
        var irA = MakeIr(one);
        var irB = MakeIr(two);

        Assert.Equal(irA.ComputeBehaviorHash(), irB.ComputeBehaviorHash());
        var docA = CanonicalSbobEmitter.Emit(irA, "0.1.0", "develapp");
        var docB = CanonicalSbobEmitter.Emit(irB, "0.1.0", "develapp");
        Assert.Equal(docA, docB); // sections identical; provenance is not emitted
    }

    [Fact]
    public void ThreeDotWildcardRunsAreNeverEmitted()
    {
        var e = new ExecEvent("/usr/bin/git", new[] { "/usr/bin/git", CanonicalSbobEmitter.ExecArgsWildcard }, true, ResolutionTier.Literal,
            new[] { new Provenance("T", "f.cs", 1, ResolutionTier.Literal, "dotnet.process.start") });
        var ir = MakeIrWithExec(e);
        var yaml = CanonicalSbobEmitter.Emit(ir, "0.1.0", "develapp");
        Assert.DoesNotContain("...", yaml);
        Assert.Contains("⋯⋯", yaml);
    }

    [Fact]
    public void AllQuarantinedProfileIsRefused()
    {
        var quarantined = new CoverageReport(
            new[] { new QuarantinedItem(BehaviorPrimitive.Open, "unresolvable", "f.cs", 1, "T", "r") },
            Array.Empty<NoiseItem>(), Array.Empty<UnrepresentableItem>(), 1, 1, 1);
        var ir = new BehaviorIr(
            new PackageIdentity("nuget", "X", "1.0.0", "linux"),
            Array.Empty<ExecEvent>(), Array.Empty<OpenEvent>(),
            Array.Empty<NetEvent>(), Array.Empty<NetEvent>(),
            quarantined, "deadbeef", "cafebabe");
        Assert.Throws<EmitException>(() => CanonicalSbobEmitter.Emit(ir, "0.1.0", "develapp"));
    }

    private static BehaviorIr MakeIrWithExec(params ExecEvent[] execs) => new(
        new PackageIdentity("nuget", "Example.Core", "1.0.0", "linux"),
        execs, Array.Empty<OpenEvent>(),
        Array.Empty<NetEvent>(), Array.Empty<NetEvent>(),
        CoverageReport.Empty, "deadbeef", "cafebabe");
}
