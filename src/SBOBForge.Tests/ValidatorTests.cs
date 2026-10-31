using System.Linq;
using SBOBForge.Core.Validation;
using Xunit;

namespace SBOBForge.Tests;

public class ValidatorTests
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

    [Fact]
    public void AcceptsPackagePurlImageId()
    {
        var yaml = Header + "pkg:nuget/example.core@1.0.0\n";
        Assert.Empty(SbobValidator.Validate(yaml));
    }

    [Fact]
    public void AcceptsChocolateyPurlAndOciReferences()
    {
        Assert.Empty(SbobValidator.Validate(Header + "pkg:choco/example-core@1.0.0\n"));
        Assert.Empty(SbobValidator.Validate(Header + "registry.io/team/app@sha256:abc\n"));
    }

    [Theory]
    [InlineData("example-core")]          // neither OCI nor purl
    [InlineData("pkg:nuget@1.0.0")]       // no namespace segment
    [InlineData("pkg:nuget/example.core@")] // no version
    public void RejectsMalformedImageIds(string imageId)
    {
        var errors = SbobValidator.Validate(Header + imageId + "\n");
        Assert.Contains(errors, e => e.StartsWith("spec.imageID"));
    }

    [Fact]
    public void RejectsThreeDotArgsAndAcceptsU22EF()
    {
        var bad = Header + "pkg:nuget/example.core@1.0.0\n" +
                  "  execs:\n    - path: /usr/bin/tool\n      args: [/usr/bin/tool, ...]\n";
        Assert.Contains(SbobValidator.Validate(bad),
            e => e.Contains("three-dot run") && e.StartsWith("execs.args"));

        var good = Header + "pkg:nuget/example.core@1.0.0\n" +
                   "  execs:\n    - path: /usr/bin/tool\n      args: [/usr/bin/tool, ⋯⋯]\n";
        Assert.Empty(SbobValidator.Validate(good));
    }

    [Fact]
    public void WildcardMustOccupyWholePathSegment()
    {
        var good = Header + "pkg:nuget/example.core@1.0.0\n" +
                  "  opens:\n    - path: /app/⋯/config.json\n      flags: [O_RDONLY]\n";
        Assert.Empty(SbobValidator.Validate(good));

        var bad = Header + "pkg:nuget/example.core@1.0.0\n" +
                  "  opens:\n    - path: /app/a⋯b/config.json\n      flags: [O_RDONLY]\n";
        Assert.Contains(SbobValidator.Validate(bad),
            e => e.StartsWith("opens.path") && e.Contains("whole path segment"));
    }

    [Fact]
    public void RejectsUnknownOpenFlagsAndBadPorts()
    {
        var yaml = Header + "pkg:nuget/example.core@1.0.0\n" +
                  "  opens:\n    - path: /tmp/x\n      flags: [O_CREATX]\n" +
                  "  egress:\n    - identifier: api-x-egress\n      type: external\n      dnsNames: [\"api.x\"]\n" +
                  "      ports:\n        - { port: 99999, protocol: TCP }\n";
        var errors = SbobValidator.Validate(yaml);
        Assert.Contains(errors, e => e.Contains("O_CREATX") && e.StartsWith("opens.flags"));
        Assert.Contains(errors, e => e.StartsWith("ports.port"));
    }

    [Fact]
    public void MissingRequiredFieldsAreReported()
    {
        var errors = SbobValidator.Validate("kind: ContainerProfile\n");
        Assert.Contains(errors, e => e.StartsWith("apiVersion"));
        Assert.Contains(errors, e => e.StartsWith("metadata.name"));
        Assert.Contains(errors, e => e.StartsWith("spec.imageID"));
    }
}
